using Content.Shared.Actions;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.DoAfter;
using Content.Shared.Inventory;
using Content.Shared.Popups;
using Content.Shared.Strip;
using Robust.Shared.Containers;
using Robust.Shared.Network;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Chemistry;
using Content.Shared.Administration.Logs;
using Robust.Shared.Audio.Systems;
using Content.Shared.Chemistry.Components.SolutionManager;
using Robust.Shared.Timing;
using Content.Shared.Imperial.HardsuitInjection.Components;
using Content.Shared.Clothing.EntitySystems;

namespace Content.Shared.Imperial.HardsuitInjection.EntitySystems;

public sealed partial class InjectSystem : EntitySystem
{
    [Dependency] private readonly SharedContainerSystem _containerSystem = default!;
    [Dependency] private readonly SharedActionsSystem _actionsSystem = default!;
    [Dependency] private readonly InventorySystem _inventorySystem = default!;
    [Dependency] private readonly SharedPopupSystem _popupSystem = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfter = default!;
    [Dependency] private readonly SharedStrippableSystem _strippable = default!;
    [Dependency] private readonly IEntityManager _entManager = default!;
    [Dependency] private readonly INetManager _netManager = default!;
    [Dependency] private readonly SharedSolutionContainerSystem _solutions = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly ReactiveSystem _reactiveSystem = default!;
    [Dependency] private readonly ISharedAdminLogManager _sharedAdminLogSystem = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly ItemSlotsSystem _itemSlotsSystem = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<InjectComponent, UpdateECEvent>(OnUpdateEC);

        InitializeBaseEvents();
        InitializeActionEvents();
        InitializeDoAfterEvents();
    }

    #region Own Events

    private void OnUpdateEC(EntityUid uid, InjectComponent component, UpdateECEvent args)
    {
        var beakerUid = GetEntity(args.BeakerUid);

        if (!TryComp<SolutionContainerManagerComponent>(beakerUid, out var solutionContainerComponent)) return;
        if (!_solutions.TryGetSolution((beakerUid, solutionContainerComponent), "beaker", out var solutionEntity, out var _)) return;

        var removedSolution = _solutions.SplitSolution((beakerUid, solutionEntity.Value.Comp), args.ReagentTransfer.Value);
        args.RemovedReagentAmount = removedSolution;

        if (!TryComp<AppearanceComponent>(beakerUid, out var appearance)) return;

        _solutions.UpdateAppearance((beakerUid, solutionEntity.Value.Comp, appearance));
    }

    #endregion
}
