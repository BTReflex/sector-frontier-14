using Content.Server.Cargo.Components;
using Content.Server._NF.Cargo.Components;
using Content.Shared._NF.Bank.Components;
using Content.Shared._NF.Cargo.BUI;
using Content.Shared.Cargo;
using Content.Shared.Cargo.Events;
using Content.Shared.GameTicking;
using Content.Shared.Mobs;
using Robust.Shared.Audio;
using Robust.Shared.Map;
using System.Numerics;
using Content.Shared.Coordinates;

namespace Content.Server._NF.Cargo.Systems;

/// <summary>
/// Handles cargo pallet (sale) mechanics.
/// Based off of Wizden's CargoSystem.
/// </summary>
public sealed partial class NFCargoSystem
{
    // The maximum distance from the console to look for pallets.
    private const int DefaultPalletDistance = 8;

    private static readonly SoundPathSpecifier ApproveSound = new("/Audio/Effects/Cargo/ping.ogg");

    private void InitializeShuttle()
    {
        SubscribeLocalEvent<NFCargoPalletConsoleComponent, CargoPalletSellMessage>(OnPalletSale);
        SubscribeLocalEvent<NFCargoPalletConsoleComponent, CargoPalletAppraiseMessage>(OnPalletAppraise);
        SubscribeLocalEvent<NFCargoPalletConsoleComponent, BoundUIOpenedEvent>(OnPalletUIOpen);

        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
    }

    #region Console

    private void UpdatePalletConsoleInterface(Entity<NFCargoPalletConsoleComponent> ent) // Frontier: EntityUid<Entity
    {
        if (Transform(ent).GridUid is not EntityUid gridUid)
        {
            _ui.SetUiState(ent.Owner, CargoPalletConsoleUiKey.Sale,
            new NFCargoPalletConsoleInterfaceState(0, 0, false));
            return;
        }

        // Modify prices based on modifier.
        GetPalletGoods(ent, gridUid, out var toSell, out var amount, out var noModAmount);
        double taxMultiplier = 1.0; // Lua
        if (TryComp<MarketModifierComponent>(ent, out var priceMod))
        {
            taxMultiplier = priceMod.Mod; // Lua ammount * < taxMultiplier
        }
        amount *= taxMultiplier; // Lua
        amount += noModAmount;

        // Lua start
        var reductionText = "";
        var contributes = ent.Comp.ContributesToMarket;
        var previewBatchByProto = new Dictionary<string, int>();
        if (contributes)
        {
            var sys = ResolveRoutingSystem(ent.Owner);
            foreach (var uid in toSell)
            {
                if (!(sys?.TryGetDynamicPrototypeId(uid, out var pid) ?? false)) continue;
                var units = 1;
                if (!previewBatchByProto.TryAdd(pid, units)) previewBatchByProto[pid] += units;
            }
        }
        double afterWeighted = 1.0;
        int dynPercentInt = 0;
        if (contributes && previewBatchByProto.Count > 0)
        {
            var system = ResolveRoutingSystem(ent.Owner);
            double sumWeighted = 0;
            double totalUnits = 0;
            foreach (var (pid, count) in previewBatchByProto)
            {
                var projected = (system?.GetProjectedMultiplierAfterSale(pid, count)) ?? 1.0;
                sumWeighted += projected * count;
                totalUnits += count;
            }
            afterWeighted = totalUnits > 0 ? sumWeighted / totalUnits : 1.0;
            var dynPercent = (1.0 - afterWeighted) * 100.0;
            dynPercentInt = Math.Max(0, (int)Math.Round(dynPercent));
            reductionText = $"-{dynPercentInt}%";
        }
        double real = 0.0;
        var routingSystem = contributes ? ResolveRoutingSystem(ent.Owner) : null;
        var multiplierCache = new Dictionary<(string, int), double>();
        foreach (var uid in toSell)
        {
            var basePrice = _pricing.GetPrice(uid);
            if (basePrice <= 0) continue;
            if (HasComp<IgnoreMarketModifierComponent>(uid))
            { real += basePrice; continue; }
            if (contributes && (routingSystem?.TryGetDynamicPrototypeId(uid, out var pid) ?? false))
            {
                var units = 1;
                var batch = previewBatchByProto.GetValueOrDefault(pid, units);
                if (!multiplierCache.TryGetValue((pid, batch), out var dyn))
                {
                    dyn = (routingSystem?.GetEffectiveMultiplierForBatch(pid, batch)) ?? 1.0;
                    multiplierCache[(pid, batch)] = dyn;
                }
                var taxed = basePrice * taxMultiplier * dyn;
                var minAfterTax = basePrice * routingSystem!.GetDynamicMinAfterTaxBaseFraction(pid);
                real += Math.Max(minAfterTax, taxed);
            }
            else
            {
                var taxed = basePrice * taxMultiplier;
                if (contributes)
                {
                    var minAfterTax = basePrice * routingSystem!.GetDynamicMinAfterTaxBaseFraction();
                    real += Math.Max(minAfterTax, taxed);
                }
                else
                { real += taxed; }
            }
        }
        var minimalUi = !ent.Comp.ContributesToMarket;
        var taxEntries = new List<PalletTaxEntry>();
        if (contributes && routingSystem != null)
        {
            var seen = new HashSet<string>();
            foreach (var uid in toSell)
            {
                if (!routingSystem.TryGetDynamicPrototypeId(uid, out var pid)) continue;
                if (!seen.Add(pid)) continue;
                var basePrice = _pricing.GetPrice(uid);
                if (basePrice <= 0) continue;
                if (HasComp<IgnoreMarketModifierComponent>(uid)) continue;
                var batch = previewBatchByProto.GetValueOrDefault(pid, 1);
                var dyn = routingSystem.GetEffectiveMultiplierForBatch(pid, batch);
                var taxed = taxMultiplier * dyn;
                var minAfterTax = routingSystem.GetDynamicMinAfterTaxBaseFraction(pid);
                var effective = Math.Max(minAfterTax, taxed);
                var percent = (int) Math.Round((effective - 1.0) * 100.0);
                var name = MetaData(uid).EntityName;
                taxEntries.Add(new PalletTaxEntry(name, percent));
            }
        }
        _ui.SetUiState(ent.Owner, CargoPalletConsoleUiKey.Sale, new NFCargoPalletConsoleInterfaceState((int)amount, toSell.Count, true, reductionText, (int)real, dynPercentInt, minimalUi, taxEntries));
        // Lua end
    }

    private void OnPalletUIOpen(Entity<NFCargoPalletConsoleComponent> ent, ref BoundUIOpenedEvent args)
    {
        UpdatePalletConsoleInterface(ent);
    }

    /// <summary>
    /// Ok so this is just the same thing as opening the UI, its a refresh button.
    /// I know this would probably feel better if it were like predicted and dynamic as pallet contents change
    /// However.
    /// I dont want it to explode if cargo uses a conveyor to move 8000 pineapple slices or whatever, they are
    /// known for their entity spam i wouldnt put it past them
    /// </summary>

    private void OnPalletAppraise(Entity<NFCargoPalletConsoleComponent> ent, ref CargoPalletAppraiseMessage args)
    {
        if (_timing.CurTime < ent.Comp.NextAppraiseTime) return; // Lua
        ent.Comp.NextAppraiseTime = _timing.CurTime + TimeSpan.FromSeconds(ent.Comp.AppraiseCooldown); // Lua
        UpdatePalletConsoleInterface(ent);
    }

    #endregion

    #region Shuttle

    /// <summary>
    /// Calculates distance between two EntityCoordinates
    /// Used to check for cargo pallets around the console instead of on the grid.
    /// </summary>
    /// <param name="point1">first point to get distance between</param>
    /// <param name="point2">second point to get distance between</param>
    /// <returns></returns>
    public static double CalculateDistance(EntityCoordinates point1, EntityCoordinates point2)
    {
        var xDifference = point2.X - point1.X;
        var yDifference = point2.Y - point1.Y;

        return Math.Sqrt(xDifference * xDifference + yDifference * yDifference);
    }

    /// GetCargoPallets(gridUid, BuySellType.Sell) to return only Sell pads
    /// GetCargoPallets(gridUid, BuySellType.Buy) to return only Buy pads
    private List<(EntityUid Entity, CargoPalletComponent Component, TransformComponent PalletXform)> GetCargoPallets(EntityUid consoleUid, EntityUid gridUid, BuySellType requestType = BuySellType.All)
    {
        _pads.Clear();

        if (!TryComp(consoleUid, out TransformComponent? consoleXform))
            return _pads;

        var query = AllEntityQuery<CargoPalletComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var comp, out var compXform))
        {
            // Short-path easy checks
            if (compXform.ParentUid != gridUid
                || !compXform.Anchored
                || (requestType & comp.PalletType) == 0)
            {
                continue;
            }

            // Check distance on pallets
            var distance = CalculateDistance(compXform.Coordinates, consoleXform.Coordinates);
            var maxPalletDistance = DefaultPalletDistance;

            // Get the mapped checking distance from the console
            if (TryComp<NFCargoPalletConsoleComponent>(consoleUid, out var cargoShuttleComponent))
                maxPalletDistance = cargoShuttleComponent.PalletDistance;

            if (distance > maxPalletDistance)
                continue;

            _pads.Add((uid, comp, compXform));

        }

        return _pads;
    }
    #endregion

    #region Station

    private bool SellPallets(Entity<NFCargoPalletConsoleComponent> consoleUid, EntityUid gridUid, out double amount, out double noMultiplierAmount) // Frontier: first arg to Entity, add noMultiplierAmount
    {
        GetPalletGoods(consoleUid, gridUid, out var toSell, out amount, out noMultiplierAmount);

        Log.Debug($"Cargo sold {toSell.Count} entities for {amount} (plus {noMultiplierAmount} without mods)");

        if (toSell.Count == 0)
            return false;

        var ev = new NFEntitySoldEvent(toSell, gridUid, consoleUid.Owner); // Lua add: consoleUid.Owner
        RaiseLocalEvent(ref ev);

        foreach (var ent in toSell)
            Del(ent);

        return true;
    }

    private void GetPalletGoods(Entity<NFCargoPalletConsoleComponent> consoleUid, EntityUid gridUid, out HashSet<EntityUid> toSell, out double amount, out double noMultiplierAmount) // Frontier: first arg to Entity, add noMultiplierAmount
    {
        amount = 0;
        noMultiplierAmount = 0;
        toSell = new HashSet<EntityUid>();

        foreach (var (palletUid, _, _) in GetCargoPallets(consoleUid, gridUid, BuySellType.Sell))
        {
            // Containers should already get the sell price of their children so can skip those.
            _setEnts.Clear();

            _lookup.GetEntitiesIntersecting(palletUid, _setEnts,
                LookupFlags.Dynamic | LookupFlags.Sundries);

            foreach (var ent in _setEnts)
            {
                // Dont sell:
                // - anything already being sold
                // - anything anchored (e.g. light fixtures)
                // - anything blacklisted (e.g. players).
                if (toSell.Contains(ent) ||
                    _xformQuery.TryGetComponent(ent, out var xform) &&
                    (xform.Anchored || !CanSell(ent, xform)))
                {
                    continue;
                }

                if (_whitelist.IsWhitelistFail(consoleUid.Comp.Whitelist, ent))
                    continue;

                if (_blacklistQuery.HasComponent(ent))
                    continue;

                var price = _pricing.GetPrice(ent);
                if (price == 0)
                    continue;
                toSell.Add(ent);

                // Check for items that are immune to market modifiers
                if (HasComp<IgnoreMarketModifierComponent>(ent))
                    noMultiplierAmount += price;
                else
                    amount += price;
            }
        }
    }

    private bool CanSell(EntityUid uid, TransformComponent xform)
    {
        // Look for blacklisted items and stop the selling of the container.
        if (_blacklistQuery.HasComponent(uid))
            return false;

        // Allow selling dead mobs
        if (_mobQuery.TryComp(uid, out var mob) && mob.CurrentState != MobState.Dead)
            return false;

        // NOTE: no bounties for now
        // var complete = IsBountyComplete(uid, out var bountyEntities);

        // Recursively check for mobs at any point.
        var children = xform.ChildEnumerator;
        while (children.MoveNext(out var child))
        {
            // NOTE: no bounties for now
            // if (complete && bountyEntities.Contains(child))
            //     continue;

            if (!CanSell(child, _xformQuery.GetComponent(child)))
                return false;
        }

        return true;
    }

    private void OnPalletSale(Entity<NFCargoPalletConsoleComponent> ent, ref CargoPalletSellMessage args)
    {
        if (!TryComp(ent, out TransformComponent? xform))
            return;

        if (xform.GridUid is not EntityUid gridUid)
        {
            _ui.SetUiState(ent.Owner, CargoPalletConsoleUiKey.Sale,
            new NFCargoPalletConsoleInterfaceState(0, 0, false));
            return;
        }

        //if (!SellPallets(ent, gridUid, out var price, out var noMultiplierPrice)) // Lua
        GetPalletGoods(ent, gridUid, out var toSellNow, out _, out _); // Lua
        if (toSellNow.Count == 0) // Lua
            return;

        // Handle market modifiers & immune objects
        double taxMultiplier = 1.0; // Lua
        if (TryComp<MarketModifierComponent>(ent, out var priceMod))
            taxMultiplier = priceMod.Mod; // Lua
        // Lua start
        var previewBatchByProto = new Dictionary<string, int>();
        var contributesSale = ent.Comp.ContributesToMarket;
        if (contributesSale)
        {
            var sysPreview = ResolveRoutingSystem(ent.Owner);
            foreach (var uid in toSellNow)
            {
                if (!(sysPreview?.TryGetDynamicPrototypeId(uid, out var pid) ?? false)) continue;
                var units = 1;
                if (!previewBatchByProto.TryAdd(pid, units)) previewBatchByProto[pid] += units;
            }
        }
        double finalPrice = 0.0;
        var sysSale = ResolveRoutingSystem(ent.Owner);
        foreach (var uid in toSellNow)
        {
            var basePrice = _pricing.GetPrice(uid);
            if (basePrice <= 0) continue;
            if (HasComp<IgnoreMarketModifierComponent>(uid))
            { finalPrice += basePrice; continue; }
            if (contributesSale && (sysSale?.TryGetDynamicPrototypeId(uid, out var pid) ?? false))
            {
                var batchUnits = previewBatchByProto.GetValueOrDefault(pid, 1);
                var dyn = (sysSale?.GetEffectiveMultiplierForBatch(pid, batchUnits)) ?? 1.0;
                var taxed = basePrice * taxMultiplier * dyn;
                var minAfterTax = basePrice * (sysSale?.GetDynamicMinAfterTaxBaseFraction(pid) ?? 0.25);
                finalPrice += Math.Max(minAfterTax, taxed);
            }
            else
            {
                var taxed = basePrice * taxMultiplier;
                if (contributesSale)
                {
                    var minAfterTax = basePrice * (sysSale?.GetDynamicMinAfterTaxBaseFraction() ?? 0.25);
                    finalPrice += Math.Max(minAfterTax, taxed);
                }
                else
                {
                    finalPrice += taxed;
                }
            }
        }
        var bulkByProto = new Dictionary<string, int>();
        if (contributesSale)
        {
            var sys = ResolveRoutingSystem(ent.Owner);
            foreach (var uid in toSellNow)
            {
                if (sys?.TryGetDynamicPrototypeId(uid, out var pid) ?? false)
                {
                    const int units = 1;
                    if (!bulkByProto.TryAdd(pid, units)) bulkByProto[pid] += units;
                }
            }
        }
        if (!SellPallets(ent, gridUid, out _, out _)) return;
        var price = finalPrice;
        // Lua end

        var stackPrototype = _proto.Index(ent.Comp.CashType);
        var stackUid = _stack.Spawn((int)price, stackPrototype, args.Actor.ToCoordinates());
        if (!_hands.TryPickupAnyHand(args.Actor, stackUid))
            _transform.SetLocalRotation(stackUid, Angle.Zero); // Orient these to grid north instead of map north
        _audio.PlayPvs(ApproveSound, ent);
        // Lua start
        if (contributesSale)
        {
            var sys = ResolveRoutingSystem(ent.Owner);
            foreach (var (pid, count) in bulkByProto) sys?.ApplyBulkSaleEffect(pid, count);
        }
        // Lua end
        UpdatePalletConsoleInterface(ent);
    }

    #endregion
}

/// <summary>
/// Event broadcast raised by-ref before it is sold and
/// deleted but after the price has been calculated.
/// </summary>
[ByRefEvent]
public readonly record struct NFEntitySoldEvent(HashSet<EntityUid> Sold, EntityUid Grid, EntityUid SourceConsole); // Lua add: EntityUid SourceConsol
