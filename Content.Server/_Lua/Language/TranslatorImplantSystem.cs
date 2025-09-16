using Content.Shared.Implants.Components;
using Content.Shared._Lua.Language;
using Content.Shared._Lua.Language.Components;
using Robust.Shared.Containers;

namespace Content.Server._Lua.Language;

public sealed class TranslatorImplantSystem : EntitySystem
{
    [Dependency] private readonly LanguageSystem _language = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<TranslatorImplantComponent, EntGotInsertedIntoContainerMessage>(OnImplant);
        SubscribeLocalEvent<TranslatorImplantComponent, EntGotRemovedFromContainerMessage>(OnDeImplant);
        SubscribeLocalEvent<ImplantedComponent, DetermineEntityLanguagesEvent>(OnDetermineLanguages);
    }

    private void OnImplant(EntityUid uid, TranslatorImplantComponent component, EntGotInsertedIntoContainerMessage args)
    {
        if (args.Container.ID != ImplanterComponent.ImplantSlotId)
            return;

        var implantee = Transform(uid).ParentUid;
        if (implantee is not { Valid: true } || !TryComp<LanguageKnowledgeComponent>(implantee, out var knowledge))
            return;

        component.Enabled = true;
        component.SpokenRequirementSatisfied = TranslatorSystem.CheckLanguagesMatch(
            component.RequiredLanguages,
            knowledge.SpokenLanguages,
            component.RequiresAllLanguages);

        component.UnderstoodRequirementSatisfied = TranslatorSystem.CheckLanguagesMatch(
            component.RequiredLanguages,
            knowledge.UnderstoodLanguages,
            component.RequiresAllLanguages);

        _language.UpdateEntityLanguages(implantee);
    }

    private void OnDeImplant(EntityUid uid,
        TranslatorImplantComponent component,
        EntGotRemovedFromContainerMessage args)
    {
        component.Enabled = component.SpokenRequirementSatisfied = component.UnderstoodRequirementSatisfied = false;

        if (TryComp<SubdermalImplantComponent>(uid, out var subdermal) &&
            subdermal.ImplantedEntity is { Valid: true } implantee)
            _language.UpdateEntityLanguages(implantee);
    }

    private void OnDetermineLanguages(EntityUid uid, ImplantedComponent component, ref DetermineEntityLanguagesEvent args)
    {
        foreach (var implant in component.ImplantContainer.ContainedEntities)
        {
            if (!TryComp<TranslatorImplantComponent>(implant, out var translator) || !translator.Enabled)
                continue;

            if (translator.SpokenRequirementSatisfied)
            {
                foreach (var language in translator.SpokenLanguages)
                {
                    args.SpokenLanguages.Add(language);
                }
            }

            if (translator.UnderstoodRequirementSatisfied)
            {
                foreach (var language in translator.UnderstoodLanguages)
                {
                    args.UnderstoodLanguages.Add(language);
                }
            }
        }
    }
}
