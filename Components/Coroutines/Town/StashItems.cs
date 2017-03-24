﻿using System;
using Trinity.Framework;
using Trinity.Framework.Helpers;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Buddy.Coroutines;
using Trinity.Framework.Actors.ActorTypes;
using Trinity.Framework.Events;
using Trinity.Framework.Objects.Enums;
using Trinity.Framework.Reference;
using Zeta.Bot;
using Zeta.Bot.Navigation;
using Zeta.Bot.Settings;
using Zeta.Game;
using Zeta.Game.Internals;
using Zeta.Game.Internals.Actors;
using Zeta.Game.Internals.Actors.Gizmos;

namespace Trinity.Components.Coroutines.Town
{
    public static class StashItems
    {
        static StashItems()
        {
            GameEvents.OnWorldChanged += (sender, args) => Cache.Clear();
            BotMain.OnStop += bot => Cache.Clear();
        }

        private static readonly Dictionary<int, bool> Cache = new Dictionary<int, bool>();

        public static bool ShouldStash(TrinityItem i)
        {
            if (i.IsUnidentified)
                return true;

            if (Core.Player.IsInventoryLockedForGreaterRift)
                return false;

            if (Cache.ContainsKey(i.AnnId))
                return Cache[i.AnnId];

            if (i.IsProtected())
                return false;

            var decision = Combat.Combat.Loot.ShouldStash(i);
            Cache.Add(i.AnnId, decision);
            return decision;
        }

        public static async Task<bool> Execute(bool dontStashCraftingMaterials = false)
        {
            if (!ZetaDia.IsInTown)
            {
                Core.Logger.Verbose("[StashItems] Need to be in town to stash items");
                return false;
            }

            var stashItems = Core.Inventory.Backpack.Where(ShouldStash).Where(i => AllowedToStash(dontStashCraftingMaterials, i)).ToList();
            if (!stashItems.Any())
            {
                Core.Logger.Verbose($"[StashItems] Nothing to stash");
                return false;
            }

            Core.Logger.Verbose($"[StashItems] Now to stash {stashItems.Count} items");
            stashItems.ForEach(i => Core.Logger.Debug($"[StashItems] Stashing: {i.Name} ({i.ActorSnoId}) InternalName={i.InternalName} Ancient={i.IsAncient} Ann={i.AnnId}"));

            GameUI.CloseVendorWindow();

            await MoveToStash();

            if (!UIElements.StashWindow.IsVisible)
            {
                var stash = ZetaDia.Actors.GetActorsOfType<GizmoPlayerSharedStash>().FirstOrDefault();
                if (stash == null)
                    return false;

                if (!await MoveTo.Execute(stash.Position))
                {
                    Core.Logger.Error($"[SalvageItems] Failed to move to stash interact position ({stash.Name}) to stash items :(");
                    return false;
                };
                Navigator.PlayerMover.MoveTowards(stash.Position);
                if (!await MoveToAndInteract.Execute(stash, 5f))
                {
                    Core.Logger.Error($"[SalvageItems] Failed to move to stash ({stash.Name}) to stash items :(");
                    return false;
                };
                await Coroutine.Sleep(750);
                stash.Interact();
            }

            if (UIElements.StashWindow.IsVisible)
            {
                if (Core.Settings.Items.BuyStashTabs && StashPagesAvailableToPurchase)
                {
                    Core.Logger.Error("[StashItems] Attempting to buy stash pages");
                    InventoryManager.BuySharedStashSlots();
                }

                await StackRamaladnisGift();
                await StackCraftingMaterials();

                var isStashFull = false;

                // Get items again to make sure they are valid and current this tick
                var freshItems = Core.Inventory.Backpack.Where(ShouldStash).Where(i => AllowedToStash(dontStashCraftingMaterials, i)).ToList();
                if (!freshItems.Any())
                {
                    Core.Logger.Verbose($"[StashItems] No items to stash");
                }
                else
                {
                    foreach (var item in freshItems)
                    {
                        try
                        {
                            int col;
                            int row;

                            var page = GetBestStashLocation(item, out col, out row);
                            if (page == -1)
                            {
                                Core.Logger.Verbose($"[StashItems] No place to put item, stash is probably full ({item.Name} [{col},{row}] Page={page})");
                                HandleFullStash();
                                isStashFull = true;
                                continue;
                            }

                            if (page != InventoryManager.CurrentStashPage)
                            {
                                Core.Logger.Verbose($"[StashItems] Changing to stash page: {page}");
                                InventoryManager.SwitchStashPage(page);
                                await Coroutine.Sleep(500);
                            }

                            Core.Logger.Verbose($"[StashItems] Stashing: {item.Name} ({item.ActorSnoId}) [{item.InventoryColumn},{item.InventoryRow} {item.InventorySlot}] Quality={item.ItemQualityLevel} IsAncient={item.IsAncient} InternalName={item.InternalName} StashPage={page}");
                            InventoryManager.MoveItem(item.AnnId, Core.Player.MyDynamicID, InventorySlot.SharedStash, col, row);

                            UpdateAfterItemMove(item);

                            ItemEvents.FireItemStashed(item);
                            await Coroutine.Sleep(500);
                        }
                        catch (Exception ex)
                        {
                            Core.Logger.Log($"Exception Stashing Item: {ex}");
                        }
                    }
                }

                await Coroutine.Sleep(1000);
                await RepairItems.Execute();

                if (isStashFull)
                    return false;

                return true;
            }

            Core.Logger.Error($"[StashItems] Failed to stash items");
            return false;
        }

        private static void UpdateAfterItemMove(TrinityItem item)
        {
            if (item.IsValid && item.CommonData.IsValid && !item.CommonData.IsDisposed)
            {
                item.OnCreated();
            }
            Core.Actors.Update();
        }

        public static async Task<bool> MoveToStash()
        {
            var stash = TownInfo.Stash;
            if (stash == null)
            {
                Core.Logger.Error("[StashItems] Unable to find a stash info for this area :(");
                return false;
            }

            if (!UIElements.StashWindow.IsVisible)
            {
                if (!await MoveToAndInteract.Execute(stash))
                {
                    Core.Logger.Error($"[StashItems] Failed to move to stash ({stash.Name}) to salvage items :(");
                    return false;
                }
                await Coroutine.Sleep(1000);
            }
            return true;
        }

        public static async Task<bool> StackRamaladnisGift()
        {
            var items = Core.Inventory.Stash.Where(i => i.RawItemType == RawItemType.GeneralUtility && !i.IsTradeable).ToList();
            if (!items.Any())
                return false;

            var targetStack = items.OrderBy(i => i.ItemStackQuantity).First();
            foreach (var item in items)
            {
                if (Equals(item, targetStack))
                    continue;

                InventoryManager.MoveItem(item.AnnId, Core.Player.MyDynamicID, InventorySlot.SharedStash, targetStack.InventoryColumn, targetStack.InventoryRow);
                await Coroutine.Sleep(100);
            }

            return true;
        }

        public static int GetStashPage(TrinityItem item)
        {
            if (item.InventorySlot != InventorySlot.SharedStash) return -1;
            return (int)Math.Floor(item.InventoryRow / 10d);
        }

        public static TrinityItem GetItemAtLocation(int col, int row)
        {
            return Core.Inventory.Stash.FirstOrDefault(i => i.InventoryRow == row && i.InventoryColumn == col);
        }

        public static TrinityItem GetNextStashItem(int currentCol, int currentRow, int actorSnoId = -1)
        {
            if (actorSnoId > 0)
            {
                var nextItemOfType = Core.Inventory.Stash.FirstOrDefault(i =>
                    i.ActorSnoId == actorSnoId &&
                    i.InventoryRow > currentRow || i.InventoryRow == currentRow && i.InventoryColumn > currentCol);

                if (nextItemOfType != null)
                {
                    return nextItemOfType;
                }
            }
            return Core.Inventory.Stash.FirstOrDefault(i => !i.IsTwoSquareItem && i.InventoryRow > currentRow || i.InventoryRow == currentRow && i.InventoryColumn > currentCol);
        }

        public static async Task<bool> StackCraftingMaterials()
        {
            var items = GetInventoryMap();

            foreach (var itemGroup in Core.Inventory.Stash.Where(i => i.MaxStackCount > 0 && i.ItemStackQuantity < i.MaxStackCount && !i.IsTradeable).GroupBy(i => i.Name))
            {
                if (itemGroup.Count() <= 1)
                    continue;

                Core.Logger.Verbose($">> Stacking: {itemGroup.Key}");

                foreach (var item in itemGroup.OrderBy(i => i.InventoryRow))
                {
                    int col = 0, row = 0;
                    var page = GetIdealStashPage(item);
                    if (page == -1)
                        page = GetStashPage(item);

                    if (CanStackOnPage(item, page, ref col, ref row, items))
                    {
                        Core.Logger.Verbose($"[StashItems] Stashing: {item.Name} ({item.ActorSnoId}) Quality={item.ItemQualityLevel} IsAncient={item.IsAncient} InternalName={item.InternalName} StashPage={page}");
                        InventoryManager.MoveItem(item.AnnId, Core.Player.MyDynamicID, InventorySlot.SharedStash, col, row);
                        await Coroutine.Sleep(100);
                    }

                    UpdateAfterItemMove(item);
                    items = GetInventoryMap();
                }
            }
            return true;
        }

        public static IEnumerable<TrinityItem> GetItemsOnStashPage(int page)
        {
            return Core.Inventory.Stash.Where(i => i.InventoryRow >= page * 10 && i.InventoryRow < page * 10 + 10);
        }

        //public static async Task<bool> SortStashPages()
        //{
        //    if (!Core.Settings.Loot.TownRun.SortStashPages)
        //        return false;

        //    if (Core.Settings.Loot.TownRun.StashGemsOnSecondToLastPage)
        //    {
        //        var secondLastPageIndex = TotalStashPages - 2;
        //        InventoryManager.SwitchStashPage(secondLastPageIndex);
        //        await SortStashPage(secondLastPageIndex);
        //    }

        //    var lastPageIndex = TotalStashPages - 1;
        //    InventoryManager.SwitchStashPage(lastPageIndex);
        //    await SortStashPage(lastPageIndex);
        //    return true;
        //}

        //public static async Task<bool> SortStashPage(int page)
        //{
        //    if (!await MoveToStash())
        //        return false;

        //    var targetCol = 0;
        //    var rowOnPage = 0;
        //    var map = GetInventoryMap();

        //    var itemsMovedAway = new List<TrinityItem>();
        //    var items = GetItemsOnStashPage(page).ToList();
        //    var equipment = items.Where(i => i.IsEquipment).OrderBy(i => i.RawItemType).ThenBy(i => i.ActorSnoId);
        //    var other = items.Where(i => !i.IsEquipment).OrderBy(i => i.RawItemType).ThenBy(i => i.ActorSnoId);
        //    var orderedItems = new Queue<TrinityItem>(other).AddRange(equipment);

        //    // Step through each square on the stash page.
        //    while (orderedItems.Any())
        //    {
        //        var itemToMove = orderedItems.Dequeue();
        //        var targetRow = page * 10 + rowOnPage;

        //        TrinityItem itemInTargetLocation, itemInLocationBelow, itemInLocationAbove;

        //        if (targetCol > 6 || rowOnPage > 9)
        //            break;

        //        // ignore target spots if the current item is already there.
        //        if (!map.TryGetValue(new Tuple<int, int>(targetCol, targetRow), out itemInTargetLocation) || itemInTargetLocation.AnnId != itemToMove.AnnId)
        //        {
        //            if (CanSwapOrPlaceAtLocation(targetCol, targetRow, itemToMove.IsTwoSquareItem, map))
        //            {
        //                Core.Logger.Verbose($"[StashItems] Sorting: {itemToMove.Name} ({itemToMove.ActorSnoId}) StashPage={page} from [{itemToMove.InventoryColumn},{itemToMove.InventoryRow}] to [{targetCol},{targetRow}]");
        //                InventoryManager.MoveItem(itemToMove.AnnId, Core.Player.MyDynamicID, InventorySlot.SharedStash, targetCol, targetRow);
        //                await Coroutine.Sleep(25);
        //                await ActorManager.WaitForUpdate();
        //                itemInTargetLocation?.Refresh();
        //                itemToMove.Refresh();
        //                map = GetInventoryMap();
        //            }
        //            else if (itemInTargetLocation != null)
        //            {
        //                // Move an item away from the target square.
        //                var moveToLocation = GetNextEmptySquare(targetCol, targetRow, itemInTargetLocation.IsTwoSquareItem);
        //                Core.Logger.Verbose($"[StashItems] >> Moving Item out of the way: {itemInTargetLocation.Name} ({itemInTargetLocation.ActorSnoId}) StashPage={Math.Floor(moveToLocation.Row / 10d)} from [{itemInTargetLocation.InventoryColumn},{itemInTargetLocation.InventoryRow}] to [{moveToLocation.Column},{moveToLocation.Row}]");
        //                InventoryManager.MoveItem(itemInTargetLocation.AnnId, Core.Player.MyDynamicID, InventorySlot.SharedStash, moveToLocation.Column, moveToLocation.Row);
        //                Core.Logger.Verbose($"[StashItems] Sorting: {itemToMove.Name} ({itemToMove.ActorSnoId}) StashPage={page} from [{itemToMove.InventoryColumn},{itemToMove.InventoryRow}] to [{targetCol},{targetRow}]");
        //                InventoryManager.MoveItem(itemToMove.AnnId, Core.Player.MyDynamicID, InventorySlot.SharedStash, targetCol, targetRow);
        //                await Coroutine.Sleep(25);
        //                await ActorManager.WaitForUpdate();
        //                itemsMovedAway.Add(itemInTargetLocation);
        //                itemInTargetLocation.Refresh();
        //                itemToMove.Refresh();
        //                map = GetInventoryMap();
        //            }
        //            else if (itemToMove.IsTwoSquareItem && map.TryGetValue(new Tuple<int, int>(targetCol, targetRow + 1), out itemInLocationBelow))
        //            {
        //                // Move item in lower half of the needed 2-square space.
        //                var moveToLocation = GetNextEmptySquare(targetCol, targetRow, itemInLocationBelow.IsTwoSquareItem);
        //                Core.Logger.Verbose($"[StashItems] >> Moving Item out of the way (below): {itemInLocationBelow.Name} ({itemInLocationBelow.ActorSnoId}) StashPage={Math.Floor(moveToLocation.Row / 10d)} from [{itemInLocationBelow.InventoryColumn},{itemInLocationBelow.InventoryRow}] to [{moveToLocation.Column},{moveToLocation.Row}]");
        //                InventoryManager.MoveItem(itemInLocationBelow.AnnId, Core.Player.MyDynamicID, InventorySlot.SharedStash, moveToLocation.Column, moveToLocation.Row);
        //                Core.Logger.Verbose($"[StashItems] Sorting: {itemToMove.Name} ({itemToMove.ActorSnoId}) StashPage={page} from [{itemToMove.InventoryColumn},{itemToMove.InventoryRow}] to [{targetCol},{targetRow}]");
        //                InventoryManager.MoveItem(itemToMove.AnnId, Core.Player.MyDynamicID, InventorySlot.SharedStash, targetCol, targetRow);
        //                await Coroutine.Sleep(25);
        //                await ActorManager.WaitForUpdate();
        //                itemsMovedAway.Add(itemInLocationBelow);
        //                itemInLocationBelow.Refresh();
        //                itemToMove.Refresh();
        //                map = GetInventoryMap();
        //            }
        //            else
        //            {
        //                if (rowOnPage > 0 && map.TryGetValue(new Tuple<int, int>(targetCol, targetRow - 1), out itemInLocationAbove) && itemInLocationAbove.IsTwoSquareItem)
        //                {
        //                    // Target location was blocked by a two-square item above.
        //                    orderedItems.InsertItem(itemToMove);
        //                    Core.Logger.Verbose($"[StashItems] {itemToMove.Name}'s Target [{targetCol},{targetRow}] is blocked by {itemInLocationAbove.Name} ({itemInLocationAbove.ActorSnoId})");
        //                }
        //                else
        //                {
        //                    Core.Logger.Log($"[StashItems] Unknown: {itemToMove.Name} ({itemToMove.ActorSnoId}) StashPage={page} this=[{itemToMove.InventoryColumn},{itemToMove.InventoryRow}] Target=[{targetCol},{targetRow}]");
        //                }
        //            }
        //        }
        //        if (targetCol >= 6)
        //        {
        //            targetCol = 0;
        //            rowOnPage++;
        //        }
        //        else targetCol = targetCol + 1;
        //    }

        //    foreach (var item in itemsMovedAway)
        //    {
        //        if (GetStashPage(item) == page)
        //        {
        //            await ActorManager.WaitForUpdate();
        //            continue;
        //        }

        //        int col, row;
        //        if (!CanPutItemInStashPage(item, page, out col, out row))
        //        {
        //            GetBestStashLocation(item, out col, out row);
        //        }

        //        Core.Logger.Verbose($"[StashItems] << Restoring: {item.Name} ({item.ActorSnoId}) StashPage={page} from [{item.InventoryColumn},{item.InventoryRow}] to [{col},{row}]");
        //        InventoryManager.MoveItem(item.AnnId, Core.Player.MyDynamicID, InventorySlot.SharedStash, col, row);
        //    }

        //    return true;
        //}

        /// <summary>
        /// Get the stash page where items should ideally be placed, ignoring if it can actually be placed there.
        /// </summary>
        public static int GetIdealStashPage(TrinityItem item)
        {
            if (item.IsEquipment && Core.Settings.Items.UseTypeStashingEquipment && ItemTypeMap.ContainsKey(item.RawItemType))
            {
                return ItemTypeMap[item.RawItemType];
            }
            else if (Core.Settings.Items.UseTypeStashingOther && ItemTypeMap.ContainsKey(item.RawItemType))
            {
                return ItemTypeMap[item.RawItemType];
            }
            if (item.ItemBaseType >= ItemBaseType.Misc)
            {
                return TotalStashPages - 1;
            }
            return -1;
        }

        private static bool AllowedToStash(bool dontStashCraftingMaterials, TrinityItem i)
        {
            return !dontStashCraftingMaterials || !i.IsCraftingReagent;
        }

        //static Dictionary<RawItemType, int> ItemLocationMap = new Dictionary<RawItemType, int>
        //{
        //    { RawItemType.Gem, -1 },
        //    { RawItemType.UpgradeableJewel, -1 },
        //    //{ RawItemType.PortalDevice, -1 },
        //    //{ RawItemType.Potion, -1 },
        //};

        private static DateTime _lastTypeMapUpdate = DateTime.MinValue;
        private static Dictionary<RawItemType, int> _itemTypeMap;

        private static Dictionary<RawItemType, int> ItemTypeMap
        {
            get
            {
                if (_itemTypeMap == null || DateTime.UtcNow.Subtract(_lastTypeMapUpdate) > TimeSpan.FromMinutes(1))
                {
                    Core.Logger.Verbose("Creating Stashing ItemTypeMap");
                    var typeMap = new Dictionary<RawItemType, int>();
                    var map = GetInventoryMap();
                    foreach (var item in map)
                    {
                        var type = item.Value.RawItemType;
                        if (!typeMap.ContainsKey(type))
                        {
                            var x = item.Key.Item1;
                            var y = item.Key.Item2;
                            var page = y / 10;
                            typeMap.Add(type, page);
                            Core.Logger.Verbose($"Type: {type} => Page: {page}");
                        }
                    }
                    _itemTypeMap = typeMap;
                    _lastTypeMapUpdate = DateTime.UtcNow;
                }
                return _itemTypeMap;
            }
        }

        public static int GetBestStashLocation(TrinityItem item, out int col, out int row)
        {
            col = 0;
            row = 0;

            var stashPageOffset = GetIdealStashPage(item);
            if (stashPageOffset != -1)
            {
                int stashpage;
                if (stashPageOffset < 0)
                {
                    stashpage = (-1 + TotalStashPages) + stashPageOffset;
                }
                else if (stashPageOffset > TotalStashPages - 1)
                {
                    stashpage = TotalStashPages - 1;
                }
                else
                {
                    stashpage = stashPageOffset;
                }

                if (CanPutItemInStashPage(item, stashpage, out col, out row))
                {
                    return stashpage;
                }
            }
            if (item.ItemBaseType >= ItemBaseType.Misc)
            {
                for (int i = TotalStashPages - 1; i >= 0; i--)
                {
                    if (CanPutItemInStashPage(item, i, out col, out row))
                    {
                        return i;
                    }
                }
            }
            else
            {
                for (int j = 0; j < TotalStashPages - 1; j++)
                {
                    if (CanPutItemInStashPage(item, j, out col, out row))
                    {
                        return j;
                    }
                }
            }
            return -1;
        }

        private static HashSet<RawItemType> _specialCaseNonStackableItems = new HashSet<RawItemType>
        {
            RawItemType.CraftingPlan,
            RawItemType.CraftingPlan_Jeweler,
            RawItemType.CraftingPlanLegendary_Smith,
            RawItemType.CraftingPlan_Mystic,
            RawItemType.CraftingPlan_MysticTransmog,
            RawItemType.CraftingPlan_Smith,
        };

        public static bool CanPutItemInStashPage(TrinityItem item, int stashPageNumber, out int col, out int row)
        {
            var itemsOnStashPage = GetInventoryMap();

            col = 0;
            row = 0;

            if (stashPageNumber == 0 && !UIElements.StashDialogMainPageTab1.IsEnabled)
            {
                return false;
            }
            if (stashPageNumber == 1 && !UIElements.StashDialogMainPageTab2.IsEnabled)
            {
                return false;
            }
            if (stashPageNumber == 2 && !UIElements.StashDialogMainPageTab3.IsEnabled)
            {
                return false;
            }
            if (stashPageNumber == 3 && !UIElements.StashDialogMainPageTab4.IsEnabled)
            {
                return false;
            }
            if (stashPageNumber == 4 && !UIElements.StashDialogMainPageTab5.IsEnabled)
            {
                return false;
            }
            if (CharacterSettings.Instance.ProtectedStashPages.Contains(stashPageNumber))
            {
                return false;
            }
            if (CanStackOnPage(item, stashPageNumber, ref col, ref row, itemsOnStashPage))
            {
                return true;
            }
            if (CanPlaceOnPage(item, stashPageNumber, ref col, ref row, itemsOnStashPage))
            {
                return true;
            }
            return false;
        }

        private static bool CanPlaceOnPage(TrinityItem item, int stashPageNumber, ref int col, ref int row, InventoryMap itemsOnStashPage)
        {
            for (int i = 0; i < 10; i++)
            {
                for (int c = 0; c < 7; c++)
                {
                    var r = stashPageNumber * 10 + i;

                    if (TryGetStashingLocation(item, stashPageNumber, c, r, itemsOnStashPage, ref col, ref row))
                        return true;
                }
            }
            return false;
        }

        private static bool CanStackOnPage(TrinityItem item, int stashPageNumber, ref int col, ref int row, InventoryMap itemsOnStashPage)
        {
            if (item.IsUnidentified)
                return false;

            if (item.MaxStackCount > 0 && !item.IsTradeable && !_specialCaseNonStackableItems.Contains(item.RawItemType))
            {
                for (int i = 0; i < 10; i++)
                {
                    for (int c = 0; c < 7; c++)
                    {
                        var r = stashPageNumber * 10 + i;

                        if (TryGetStackLocation(item, stashPageNumber, c, r, itemsOnStashPage, ref col, ref row))
                            return true;
                    }
                }
            }
            return false;
        }

        private static InventoryMap GetInventoryMap()
        {
            Core.Actors.Update();
            var items = Core.Inventory.Stash.ToList();
            return new InventoryMap(items.ToDictionary(k => new Tuple<int, int>(k.InventoryColumn, k.InventoryRow), v => v));
        }

        //public static InventorySquare GetNextEmptySquare(int col, int row, bool twoSquare)
        //{
        //    for (int r = row; r < TotalStashPages - 1 * 10; r++)
        //    {
        //        for (int c = col; c < 7; c++)
        //        {
        //            if (CanPlaceAtLocation(c, r, twoSquare))
        //            {
        //                return new InventorySquare((byte)c, (byte)r);
        //            }
        //        }
        //    }
        //    for (int r = 0; r <= row; r++)
        //    {
        //        for (int c = 0; c < 7; c++)
        //        {
        //            if (CanPlaceAtLocation(c, r, twoSquare))
        //            {
        //                return new InventorySquare((byte)c, (byte)r);
        //            }
        //        }
        //    }
        //    return new InventorySquare(0, 0);
        //}

        private static bool TryGetStackLocation(TrinityItem item, int stashPageNumber, int col, int row, InventoryMap map, ref int placeAtCol, ref int placeAtRow)
        {
            var loc = new Tuple<int, int>(col, row);

            if (col == placeAtCol && row == placeAtRow)
                return false;

            var isSquareEmpty = !map.ContainsKey(loc);
            if (isSquareEmpty)
                return false;

            if (item.IsTwoSquareItem)
                return false;

            if (item.MaxStackCount <= 0)
                return false;

            var existingItem = map[loc];
            var existingStackQuantity = existingItem.ItemStackQuantity;
            var itemStackQuantity = item.ItemStackQuantity;
            var newStackSize = existingStackQuantity + itemStackQuantity;

            if (item.ActorSnoId == existingItem.ActorSnoId && newStackSize <= item.MaxStackCount && item.AnnId != existingItem.AnnId)
            {
                Core.Logger.Verbose($"Can stash item {item.Name} on page {stashPageNumber} at [col={col},row={row}] (stack on existing {existingStackQuantity} + {itemStackQuantity} ({newStackSize}) / {item.MaxStackCount})");
                placeAtCol = col;
                placeAtRow = row;
                return true;
            }

            return false;
        }

        private static bool TryGetStashingLocation(TrinityItem item, int stashPageNumber, int col, int row, InventoryMap map, ref int placeAtCol, ref int placeAtRow)
        {
            var loc = new Tuple<int, int>(col, row);
            var isSquareEmpty = !map.ContainsKey(loc);

            var isLastRow = (row + 1) % 10 == 0;
            if (isLastRow && item.IsTwoSquareItem)
                return false;

            if (item.IsTwoSquareItem)
            {
                var locBelow = new Tuple<int, int>(col, row + 1);
                if (map.ContainsKey(locBelow))
                    return false;
            }

            var locAbove = new Tuple<int, int>(col, row - 1);
            var isItemAbove = map.ContainsKey(locAbove);
            if (isItemAbove && map[locAbove].IsTwoSquareItem)
                return false;

            if (isSquareEmpty)
            {
                Core.Logger.Verbose($"Can stash item {item.Name} on page {stashPageNumber} at [col={col},row={row}]");
                placeAtCol = col;
                placeAtRow = row;
                return true;
            }
            return false;
        }

        public class InventoryMap : Dictionary<Tuple<int, int>, TrinityItem>
        {
            public InventoryMap(Dictionary<Tuple<int, int>, TrinityItem> dictionary) : base(dictionary)
            {
            }
        }

        //private static bool CanPlaceAtLocation(int col, int row, bool isTwoSquare, InventoryMap map = null)
        //{
        //    if (map == null)
        //        map = GetInventoryMap();

        //    var loc = new Tuple<int, int>(col, row);
        //    var isSquareEmpty = !map.ContainsKey(loc);

        //    var isLastRow = (9 + 1) % 10 == 0;
        //    if (isLastRow && isTwoSquare)
        //    {
        //        Core.Logger.Log("LastRow CanPlaceAtLocation");
        //        return false;
        //    }

        //    if (isTwoSquare)
        //    {
        //        var locBelow = new Tuple<int, int>(col, row + 1);
        //        if (map.ContainsKey(locBelow))
        //            return false;
        //    }

        //    var locAbove = new Tuple<int, int>(col, row - 1);
        //    var isItemAbove = map.ContainsKey(locAbove);
        //    if (isItemAbove && map[locAbove].IsTwoSquareItem)
        //        return false;

        //    return isSquareEmpty;
        //}

        //private static bool CanSwapOrPlaceAtLocation(int col, int row, bool isTwoSquare, InventoryMap map = null)
        //{
        //    if (map == null)
        //        map = GetInventoryMap();

        //    var key = new Tuple<int, int>(col, row);

        //    return CanPlaceAtLocation(col, row, isTwoSquare, map) || (map.ContainsKey(key) && isTwoSquare == map[key].IsTwoSquareItem);
        //}

        private static void HandleFullStash()
        {
            Core.Logger.Error($"[StashItems] There is no space in the stash. Woops!");

            if (GlobalSettings.Instance.FullInventoryHandling == FullInventoryOption.Logout)
            {
                Core.Logger.Error($"[StashItems] Shutting down DB and D3 and requesting no restarts (DemonbuddyExitCode.DoNotRestart: 12) because of DB Setting 'FullInventoryOption.Logout'!");
                ZetaDia.Service.Party.LeaveGame(false);
                Thread.Sleep(15000);
                BotMain.Stop(false, "");
                BotMain.Shutdown(DemonbuddyExitCode.DoNotRestart, true);
            }
        }

        public static bool StashPagesAvailableToPurchase => 5 - ZetaDia.Me.CommonData.GetAttribute<int>(ActorAttributeType.StashTabsPurchasedWithGold) > 0;

        public static int TotalStashPages => ZetaDia.Me.NumSharedStashSlots / 70;
    }
}