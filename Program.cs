using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI.Ingame;
using Sandbox.ModAPI.Interfaces;
using SpaceEngineers.Game.ModAPI.Ingame;
using System;
using System.CodeDom;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Text;
using Sandbox.Game.Entities.Blocks;
using VRage;
using VRage.Collections;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.GUI.TextPanel;
using VRage.Game.ModAPI.Ingame;
using VRage.Game.ModAPI.Ingame.Utilities;
using VRage.Game.ObjectBuilders.Definitions;
using VRageMath;

namespace IngameScript
{
    public partial class Program : MyGridProgram
    {
        // Version (Used for display)
        string version = "v0.4.0";

        // Keyword management
        string lcdKeyword = "[KIM]";
        string lcdDebugKeyword = "[KIM Debug]";
        string oresTag = "[Ores]";
        string ingotsTag = "[Ingots]";
        string componentsTag = "[Components]";
        string ammoTag = "[Ammo]";
        string elseTag = "[Else]";
        string overflowTag = "[Overflow]";
        string refineTag = "[Refine]";

        // Cargo Scan Management
        class CargoContainer
        {
            public IMyCargoContainer Container;
            public IMyInventory Inventory;
            public bool OresFlag = false;
            public bool IngotsFlag = false;
            public bool ComponentsFlag = false;
            public bool AmmoFlag = false;
            public bool ElseFlag = false;
            public bool OverflowFlag = false;
            public bool RefineFlag = false;
        }
        
        List<CargoContainer> managedCargos = new List<CargoContainer>();
        
        
        
        // Setup Item Categories: {Ores, Ingots, Components, Ammo, Else}
        static string GetItemCategory(MyItemType type)
        {
            switch (type.TypeId)
            {
                case "MyObjectBuilder_Ore": return "Ores";
                case "MyObjectBuilder_Ingot": return "Ingots";
                case "MyObjectBuilder_Component": return "Components";
                case "MyObjectBuilder_AmmoMagazine": return "Ammo";
                default: return "Else";
            }
        }

        List<IMyCargoContainer> taggedCargos = new List<IMyCargoContainer>();

        IMyCargoContainer overflowCargo;

        List<IMyInventory> oreInventories = new List<IMyInventory>();
        List<IMyInventory> ingotInventories = new List<IMyInventory>();
        List<IMyInventory> componentInventories = new List<IMyInventory>();
        List<IMyInventory> ammoInventories = new List<IMyInventory>();
        List<IMyInventory> elseInventories = new List<IMyInventory>();
        List<IMyInventory> overflowInventories = new List<IMyInventory>();

        class LcdPanel
        {
            public IMyTerminalBlock Block;
            public IMyTextSurface   Surface;
            public RectangleF       Viewport;
            public MyIni            Ini = new MyIni();
            public string Mode;
        }

        List <LcdPanel> panels = new List<LcdPanel>();
        
        IEnumerator<bool> stateMachine;
        int allowedUnitsThisTick = 5; 
        int instructionCap = 25000;
        int unitsRanLastTick = 0;
        private Queue<string> taskLog = new Queue<string>();
        private int maxTaskLogEntries = 25;
        int taskIndex = 0;

        IMyCargoContainer refineCargo;
        IMyInventory refineInventory;

        public Program()
        {
            // Set Update100
            Runtime.UpdateFrequency = UpdateFrequency.Update100;

            // Get PB Custom Data
            var pbIni = new MyIni();

            // Setup Initial LCD stuff
            panels.Clear();
            var lcdBlocks = new List<IMyTerminalBlock>();
            GridTerminalSystem.GetBlocksOfType(lcdBlocks, block => block != Me && (block.CustomName.Contains(lcdKeyword) || (block.CustomName.Contains(lcdDebugKeyword))));
            foreach (var block in lcdBlocks)
            {
                var provider = block as IMyTextSurfaceProvider;
                var surface = provider.GetSurface(0);
                PrepareTextSurfaceForSprites(surface);

                if (block.CustomName.Contains(lcdDebugKeyword))
                {
                    panels.Add(new LcdPanel
                    {
                        Block = block,
                        Surface = surface,
                        Viewport =
                            new RectangleF((surface.TextureSize - surface.SurfaceSize) / 2f, surface.SurfaceSize),
                        Mode = "DEBUG"
                    });
                }
                
                if (block.CustomName.Contains(lcdKeyword))
                {
                    panels.Add(new LcdPanel
                    {
                        Block = block,
                        Surface = surface,
                        Viewport =
                            new RectangleF((surface.TextureSize - surface.SurfaceSize) / 2f, surface.SurfaceSize),
                        Mode = "None"
                    });
                }
            }
            

            // Get Cargo Blocks with tags
            GridTerminalSystem.GetBlocksOfType(taggedCargos,
                container => (container.CustomName.Contains(oresTag) || container.CustomName.Contains(ingotsTag) ||
                              container.CustomName.Contains(componentsTag) || container.CustomName.Contains(ammoTag) ||
                              container.CustomName.Contains(elseTag)) || container.CustomName.Contains(overflowTag) ||
                              container.CustomName.Contains(refineTag));

            foreach (var cargoContainer in taggedCargos)
            {
                var managedCargo = new CargoContainer
                {
                    Container = cargoContainer,
                    Inventory = cargoContainer.GetInventory(0),
                };

                if (cargoContainer.CustomName.Contains(oresTag))
                    managedCargo.OresFlag = true;

                if (cargoContainer.CustomName.Contains(ingotsTag))
                    managedCargo.IngotsFlag = true;
                
                if (cargoContainer.CustomName.Contains(componentsTag))
                    managedCargo.ComponentsFlag = true;
                
                if (cargoContainer.CustomName.Contains(ammoTag))
                    managedCargo.AmmoFlag = true;
                
                if (cargoContainer.CustomName.Contains(elseTag))
                    managedCargo.ElseFlag = true;

                if (cargoContainer.CustomName.Contains(overflowTag))
                {
                    managedCargo.OverflowFlag = true;
                    overflowCargo = managedCargo.Container;
                }

                if (cargoContainer.CustomName.Contains(refineTag))
                {
                    managedCargo.RefineFlag = true;
                    refineCargo = managedCargo.Container;
                    refineInventory = managedCargo.Inventory;
                }

                managedCargos.Add(managedCargo);
            }
            
        }

        public void Save()
        {
            
        }
        
        
        public void Main(string argument, UpdateType updateSource)
        {
            Echo($"Kippen Inventory Manager (KIM) {version}...\n");
            Echo($"Tracked Cargos: {managedCargos.Count}");
            
            if (stateMachine == null)
                stateMachine = RunStuffOverTime();
            Runtime.UpdateFrequency |= UpdateFrequency.Once;
            
            RunStateMachine();

            foreach (var panel in panels)
            {
                var frame = panel.Surface.DrawFrame();

                switch (panel.Mode)
                {
                    case "DEBUG":
                        DrawDebugLog(ref frame, panel);
                        break;
                    default:
                        DrawSprites(ref frame, panel);
                        break;
                }
                
                frame.Dispose();
            }
            
        }

        

        /// <summary>
        /// MAIN FUNCTION WHERE MOST STUFF RUNS
        /// </summary>

        public IEnumerator<bool> RunStuffOverTime()
        {
            taskIndex = 0;
            
            var timeSinceBoot = Runtime.LifetimeTicks;
            StartTask("[" + timeSinceBoot + "] " + "Expelling incorrect items to overflow cargo", taskIndex);
            taskIndex++;
            foreach (var managedCargo in managedCargos)
            {
                if (managedCargo.OverflowFlag)
                    continue;

                var items = new List<MyInventoryItem>();
                
                managedCargo.Inventory.GetItems(items);

                for (var i = items.Count - 1; i >= 0; i--)
                {
                    var itemCategory = GetItemCategory(items[i].Type);
                    if (itemCategory == "Ores" && (managedCargo.OresFlag || managedCargo.RefineFlag)) continue;
                    if (itemCategory == "Ingots" && managedCargo.IngotsFlag) continue;
                    if (itemCategory == "Components" && managedCargo.ComponentsFlag) continue;
                    if (itemCategory == "Ammo" && managedCargo.AmmoFlag) continue;
                    if (itemCategory == "Else" && managedCargo.ElseFlag) continue;
                    
                    if (!managedCargo.OverflowFlag)// && managedCargo.Inventory.CurrentVolume < managedCargo.Inventory.MaxVolume)
                    {
                        managedCargo.Inventory.TransferItemTo(overflowCargo.GetInventory(), i, null, true,
                            items[i].Amount);
                    }

                }
                
                yield return true;
                
            }
            
            timeSinceBoot = Runtime.LifetimeTicks;
            StartTask("[" + timeSinceBoot + "] " + "Getting all Inventories (not items)", taskIndex);
            taskIndex++;
            foreach (var managedCargo in managedCargos)
            {
                if (managedCargo.OverflowFlag)
                    overflowInventories.Add(managedCargo.Inventory);
                if (managedCargo.OresFlag)
                    oreInventories.Add(managedCargo.Inventory);
                if (managedCargo.IngotsFlag)
                    ingotInventories.Add(managedCargo.Inventory);
                if (managedCargo.ComponentsFlag)
                    componentInventories.Add(managedCargo.Inventory);
                if (managedCargo.AmmoFlag)
                    ammoInventories.Add(managedCargo.Inventory);
                if (managedCargo.ElseFlag)
                    elseInventories.Add(managedCargo.Inventory);

                yield return true;
                
            }
            
            timeSinceBoot = Runtime.LifetimeTicks;
            StartTask("[" + timeSinceBoot + "] " + "Internal Sorting", taskIndex);
            taskIndex++;
            
            var internalItems = new List<MyInventoryItem>();
            foreach (var managedCargo in managedCargos)
            {
                if (managedCargo.OverflowFlag) continue;

                internalItems.Clear();
                
                var managedInv = managedCargo.Inventory;
                managedInv.GetItems(internalItems);

                for (int i = internalItems.Count - 1; i >= 0; i--)
                {
                    for (int j = internalItems.Count - 1; j >= 0; j--)
                    {
                        if (internalItems[j].Type == internalItems[i].Type)
                        {
                            managedInv.TransferItemTo(managedInv, j, i, true, internalItems[j].Amount);
                        }
                    }

                    yield return true;
                }

                for (int i = 0; i < internalItems.Count - 1; i++)
                {
                    if (internalItems[i].Amount < internalItems[i + 1].Amount)
                    {
                        managedInv.TransferItemTo(managedInv, i + 1, i, false, internalItems[i].Amount);
                    }

                    yield return true;
                }
            }
            

            timeSinceBoot = Runtime.LifetimeTicks;
            StartTask("[" + timeSinceBoot + "] " + "Sorting overflow cargos", taskIndex);
            taskIndex++;
            foreach (var overflowInv in overflowInventories)
            {
                var items = new List<MyInventoryItem>();
                
                overflowInv.GetItems(items);
                for (int i = items.Count - 1; i >= 0; i--)
                {
                    string itemCategory = GetItemCategory(items[i].Type);
                    switch (itemCategory)
                    {
                        case "Ores":
                            foreach (var oreInv in oreInventories)
                            {
                                overflowInv.TransferItemTo(oreInv, i, null, true, items[i].Amount);
                            }
                            break;

                        case "Ingots":
                            foreach (var ingotInv in ingotInventories)
                            {
                                overflowInv.TransferItemTo(ingotInv, i, null, true, items[i].Amount);
                            }
                            break;

                        case "Components":
                            foreach (var componentInv in componentInventories)
                            {
                                overflowInv.TransferItemTo(componentInv, i, null, true, items[i].Amount);
                            }
                            break;

                        case "Ammo":
                            foreach (var ammoInv in ammoInventories)
                            {
                                overflowInv.TransferItemTo(ammoInv, i, null, true, items[i].Amount);
                            }
                            break;

                        case "Else":
                            foreach (var elseInv in elseInventories)
                            {
                                overflowInv.TransferItemTo(elseInv, i, null, true, items[i].Amount);
                            }

                            break;

                    }

                    yield return true;
                }
            }

            timeSinceBoot = Runtime.LifetimeTicks;
            StartTask("[" + timeSinceBoot + "] " + "Drawing LCD panels", taskIndex);
            taskIndex++;
            foreach (var panel in panels)
            {
                var frame = panel.Surface.DrawFrame();

                DrawSprites(ref frame, panel);
                
                frame.Dispose();
            }
            yield return true;

        }
        
        void StartTask(string taskName, int index)
        {
            taskLog.Enqueue(taskName);
            if (taskLog.Count > maxTaskLogEntries)
            {
                taskLog.Dequeue();
            }
        }
        
        public void RunStateMachine()
        {
            if (stateMachine == null) return;

            int unitsRan = 0;
            while (unitsRan < allowedUnitsThisTick)
            {
                if (Runtime.CurrentInstructionCount >= instructionCap) break;

                bool hasMore = stateMachine.MoveNext();
                unitsRan++;

                if (!hasMore)
                {
                    stateMachine.Dispose();
                    stateMachine = RunStuffOverTime();
                }
            }

            unitsRanLastTick = unitsRan;
            Runtime.UpdateFrequency |= UpdateFrequency.Once;
        }
    
        
        void PrepareTextSurfaceForSprites(IMyTextSurface textSurface)
        {
            textSurface.ScriptBackgroundColor = new Color(0, 0, 0, 255);
            textSurface.ContentType = ContentType.SCRIPT;
            textSurface.Script = "";
        }

        void DrawSprites(ref MySpriteDrawFrame frame, LcdPanel panel)
        {
            
            var sprite_background = new MySprite()
            {
                Type = SpriteType.TEXTURE,
                Data = "Textures\\FactionLogo\\Others\\OtherIcon_29.dds",
                Position = new Vector2(256, 256),
                Size = panel.Viewport.Size / 2f,
                Color = new Color(0, 100, 120, 50), // Teal
                Alignment = TextAlignment.CENTER
            };
            frame.Add(sprite_background);
            
        }

        void DrawDebugLog(ref MySpriteDrawFrame frame, LcdPanel panel)
        {
            var pos = new Vector2(0,0);
            float fs = 0.6f;
            float lh = fs * 26f;
            
            var entries = taskLog.ToArray();

            for (int i = entries.Length - 1; i >= 0; i--)
            {
                string line = entries[i];
                frame.Add(new MySprite
                {
                    Type = SpriteType.TEXT,
                    Data = line,
                    Position = pos,
                    RotationOrScale = fs,
                    Color = Color.White,
                    Alignment = TextAlignment.LEFT,
                    FontId = "Monospace"
                });
                pos += new Vector2(0,lh*1.05f);
            }
            
        }
        
    }
}