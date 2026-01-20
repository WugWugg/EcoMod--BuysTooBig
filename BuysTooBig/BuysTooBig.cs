

using Eco.Core;
using Eco.Core.Controller;
using Eco.Core.Items;
using Eco.Core.Properties;
using Eco.Core.PropertyHandling;
using Eco.Gameplay.Components;
using Eco.Gameplay.Components.Store;
using Eco.Gameplay.Components.Store.Internal;
using Eco.Gameplay.Items;
using Eco.Gameplay.Objects;
using Eco.Gameplay.Systems.NewTooltip;
using Eco.Gameplay.Systems.NewTooltip.TooltipLibraryFiles;
using Eco.Gameplay.Systems.TextLinks;
using Eco.Shared.IoC;
using Eco.Shared.Items;
using Eco.Shared.Localization;
using Eco.Shared.Utils;
using System.Linq;
using System.Runtime.Versioning;

namespace BuysTooBig
{
    [TooltipLibrary]
    [SupportedOSPlatform("windows7.0")]
    public static class BuysTooBig
    {
        //TODO: Add mod registration?

        public static void Initialize() {}

        public record struct BuyOfferInfo
        {
            public Item Item;
            public int AvailableSpace;
            public int MaxNumWanted;
            public bool NoCap;
        }

        [NewTooltip(Eco.Shared.Items.CacheAs.Disabled, 160)]
        public static LocString DeliveryLimitTooltip(this StoreComponent store, TooltipOrigin origin)
        {
            var itemsTillFull = MergeBuyOffers(store);
            CalculateOpenSpace(store, itemsTillFull);
            var lines = IntoLines(itemsTillFull);
            return new TooltipSection(Localizer.DoStr("Delivery Limits"), lines.FoldoutListLoc("item", origin));
        }

        /// <summary>
        /// Returns a dictionary of item types to a default BuyOfferInfo
        /// </summary>
        /// <param name="store"></param>
        /// <returns></returns>
        private static Dictionary<Type, BuyOfferInfo> MergeBuyOffers(StoreComponent store)
        {
            Dictionary<Type, BuyOfferInfo> partialSpaceRemaining = new();
            foreach (var offer in store.StoreData.BuyOffers)
            {
                var type = offer.Stack.Item.Type;
                if (!partialSpaceRemaining.TryGetValue(type, out var info))
                {
                    info = new BuyOfferInfo
                    {
                        Item = offer.Stack.Item,
                        AvailableSpace = 0,
                        MaxNumWanted = offer.MaxNumWanted,
                        NoCap = !offer.ShouldLimit
                    };
                }
                else
                {
                    info.MaxNumWanted = Math.Max(info.MaxNumWanted, offer.MaxNumWanted);
                    // AND is correct: if any offer has ShouldLimit == false (e.g., a 999 buy order),
                    // the tooltip assumes the player would choose that unlimited offer.
                    info.NoCap |= !offer.ShouldLimit;
                }
                partialSpaceRemaining[type] = info;
            }
            return partialSpaceRemaining;
        }

        /// <summary>
        /// Scans connected inventories and updates the given dictionary with available space for each item.
        /// </summary>
        /// <param name="store"></param>
        /// <param name="offersByType"></param>
        private static void CalculateOpenSpace(StoreComponent store, Dictionary<Type, BuyOfferInfo> offersByType)
        {
            // For each inventory of this store...
            foreach (var inventory in (store as IHasTradeOffers).DepositInventories)
            {
                var typesNeedingSpace = TypesNeedingSpaceForInventory(inventory, offersByType);
                // This inventory will not accept any items that are being bought. Skip it as it doesn't relate to delivery limits.
                if (typesNeedingSpace.Count == 0) continue;
                var maxPerSlot = new Dictionary<Type, int>(typesNeedingSpace.Count);
                foreach (var x in typesNeedingSpace)
                    maxPerSlot[x] = inventory.GetMaxAcceptedVal(offersByType[x].Item, 0);
                int emptySlots = 0;
                // For each stack (read: slot) in this inventory...
                foreach (var s in inventory.Stacks)
                {
                    // If this is an empty stack...
                    if (s.Quantity == 0)
                    {
                        emptySlots++;
                        continue;
                    }
                    var type = s.Item.Type;
                    // If this stack isn't empty, but is of the same item type...
                    if (!typesNeedingSpace.Contains(type)) continue;
                    // Increase the count for this specific item. Increase by the amount needed to completly fill the stack.
                    int delta = maxPerSlot[type] - s.Quantity; // Max accepted per slot
                    if (delta <= 0) continue;
                    var info = offersByType[type];
                    info.AvailableSpace += delta;
                    offersByType[type] = info;
                }
                if (emptySlots  > 0)
                {
                    foreach (var x in typesNeedingSpace)
                    {
                        var info = offersByType[x];
                        info.AvailableSpace += emptySlots * maxPerSlot[x];
                        offersByType[x] = info;
                    }
                }
            }
        }

        /// <summary>
        /// Formats dictionary into text lines for delivery limits.
        /// </summary>
        /// <param name="offersByType"></param>
        /// <returns></returns>
        private static IEnumerable<LocString> IntoLines(Dictionary<Type, BuyOfferInfo> offersByType)
        {
            List<LocString> warnings = new();
            List<LocString> stopOrders = new();
            foreach (var entry in offersByType)
            {
                int spaceLeft = entry.Value.AvailableSpace;
                if (spaceLeft == 0)
                    stopOrders.Add(Localizer.NotLocalized($"<color=red>X</color> {entry.Value.Item.UILink()}"));
                else if (entry.Value.NoCap || spaceLeft < entry.Value.MaxNumWanted)
                {
                    warnings.Add(Localizer.NotLocalized($"<color=yellow>!</color> {entry.Value.Item.UILinkAndNumber(spaceLeft)}"));
                }
            };
            if (!warnings.Any() && !stopOrders.Any())
            {
                return new List<LocString>() { Localizer.DoStr("<i>You can deliever all buy orders in full.</i>") };
            }
            List<LocString> lines = new();
            if (warnings.Any())
            {
                lines.Add(Localizer.DoStr("You can only deliver (storage is filling up):"));
                lines.AddRange(warnings);
            }
            if (stopOrders.Any())
            {
                if (lines.Any()) lines.Add(Localizer.NotLocalizedStr("<color=grey>---</color>"));
                lines.Add(Localizer.DoStr($"Storage full. You cannot deliver:"));
                lines.AddRange(stopOrders);
            }
            //lines.Add(Localizer.NotLocalized($"{DateTime.UtcNow.ToString("HH:mm:ss")}")); For cache debugging
            return lines;
        }

        /// <summary>
        /// Returns the item types that still need space calculated for this inventory.
        /// Includes uncapped buy offers (NoCap) and capped offers whose available space
        /// is still below MaxNumWanted. Filters out types this inventory cannot accept.
        /// </summary>
        /// <param name="inventory"></param>
        /// <param name="offersByType"></param>
        /// <returns></returns>
        private static HashSet<Type> TypesNeedingSpaceForInventory(
            Inventory inventory,
            Dictionary<Type, BuyOfferInfo> offersByType)
        {
            return offersByType
                .Where(kvp => kvp.Value.NoCap || kvp.Value.AvailableSpace < kvp.Value.MaxNumWanted)
                .Where(kvp => inventory.AcceptsItem(kvp.Value.Item))
                .Select(kvp => kvp.Key)
                .ToHashSet();
        }

    }
}
