using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace EmbodiedNPC.Actions
{
    public enum ActionBindingKind
    {
        EntityRef,
        Enum,
        Number,
        Boolean,
        String,
    }

    public enum ActionSelectionMode
    {
        Exact,
        Any,
        Nearest,
        Random,
    }

    public enum ActionConfirmationPolicy
    {
        None,
        BeforeExecution,
        OnRebind,
    }

    public enum ActionInterruptionPolicy
    {
        Cancel,
        Pause,
        FinishAtomicStep,
    }

    public sealed class ActionBindingDescriptor
    {
        public ActionBindingDescriptor(string name, ActionBindingKind kind, bool required = true,
            IEnumerable<string> enumValues = null, double? minimum = null, double? maximum = null,
            int? maxLength = null)
        {
            if (!ActionContractRules.IsIdentifier(name))
                throw new ArgumentException("Binding name must be a bounded identifier.", nameof(name));
            string[] values = (enumValues ?? Array.Empty<string>()).ToArray();
            if (values.Length > 32 || values.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 80) ||
                values.Distinct(StringComparer.Ordinal).Count() != values.Length)
                throw new ArgumentException("Enum values must be bounded, non-blank and unique.", nameof(enumValues));
            if (kind == ActionBindingKind.Enum && values.Length == 0)
                throw new ArgumentException("Enum bindings require allowed values.", nameof(enumValues));
            if (kind != ActionBindingKind.Enum && values.Length != 0)
                throw new ArgumentException("Only enum bindings accept enum values.", nameof(enumValues));
            if (kind == ActionBindingKind.Number && (!minimum.HasValue || !maximum.HasValue))
                throw new ArgumentException("Number bindings require a bounded range.", nameof(minimum));
            if (kind == ActionBindingKind.Number && minimum > maximum)
                throw new ArgumentException("Number binding range is invalid.", nameof(minimum));
            if (kind != ActionBindingKind.Number && (minimum.HasValue || maximum.HasValue))
                throw new ArgumentException("Only number bindings accept numeric bounds.", nameof(minimum));
            if (kind == ActionBindingKind.String && (!maxLength.HasValue || maxLength < 1 || maxLength > 500))
                throw new ArgumentException("String bindings require a bounded max length.", nameof(maxLength));
            if (kind != ActionBindingKind.String && maxLength.HasValue)
                throw new ArgumentException("Only string bindings accept max length.", nameof(maxLength));

            Name = name;
            Kind = kind;
            Required = required;
            EnumValues = values;
            Minimum = minimum;
            Maximum = maximum;
            MaxLength = maxLength;
        }

        public string Name { get; }
        public ActionBindingKind Kind { get; }
        public bool Required { get; }
        public IReadOnlyList<string> EnumValues { get; }
        public double? Minimum { get; }
        public double? Maximum { get; }
        public int? MaxLength { get; }
    }

    public sealed class ActionDescriptor
    {
        public ActionDescriptor(string actionId, string version, string description,
            IEnumerable<ActionBindingDescriptor> bindingSchema,
            IEnumerable<string> targetRequirements, bool available, float defaultTimeoutSeconds,
            ActionInterruptionPolicy interruptionPolicy, ActionConfirmationPolicy confirmationPolicy)
        {
            if (!ActionContractRules.IsIdentifier(actionId))
                throw new ArgumentException("Action ID must be a bounded identifier.", nameof(actionId));
            if (!ActionContractRules.IsVersion(version))
                throw new ArgumentException("Action version must use major.minor format.", nameof(version));
            if (string.IsNullOrWhiteSpace(description) || description.Length > 240)
                throw new ArgumentException("Action description must contain at most 240 characters.", nameof(description));
            ActionBindingDescriptor[] bindings = (bindingSchema ?? Array.Empty<ActionBindingDescriptor>()).ToArray();
            string[] requirements = (targetRequirements ?? Array.Empty<string>()).ToArray();
            if (bindings.Length > 16 || bindings.Any(value => value == null) ||
                bindings.Select(value => value.Name).Distinct(StringComparer.Ordinal).Count() != bindings.Length)
                throw new ArgumentException("Bindings must be bounded and uniquely named.", nameof(bindingSchema));
            if (requirements.Length > 16 || requirements.Any(value =>
                    string.IsNullOrWhiteSpace(value) || value.Length > 80) ||
                requirements.Distinct(StringComparer.Ordinal).Count() != requirements.Length)
                throw new ArgumentException("Target requirements must be bounded, non-blank and unique.", nameof(targetRequirements));
            if (defaultTimeoutSeconds <= 0f || defaultTimeoutSeconds > 600f)
                throw new ArgumentOutOfRangeException(nameof(defaultTimeoutSeconds));

            ActionId = actionId;
            Version = version;
            Description = description;
            BindingSchema = bindings;
            TargetRequirements = requirements;
            Available = available;
            DefaultTimeoutSeconds = defaultTimeoutSeconds;
            InterruptionPolicy = interruptionPolicy;
            ConfirmationPolicy = confirmationPolicy;
        }

        public string ActionId { get; }
        public string Version { get; }
        public string Description { get; }
        public IReadOnlyList<ActionBindingDescriptor> BindingSchema { get; }
        public IReadOnlyList<string> TargetRequirements { get; }
        public bool Available { get; }
        public float DefaultTimeoutSeconds { get; }
        public ActionInterruptionPolicy InterruptionPolicy { get; }
        public ActionConfirmationPolicy ConfirmationPolicy { get; }
    }

    public sealed class ActionRegistration
    {
        public ActionRegistration(ActionDescriptor descriptor, IActionBinder binder = null,
            IActionAdmissionPolicy admissionPolicy = null, IActionPlanFactory planFactory = null,
            IActionCandidateProvider candidateProvider = null, IActionRecoveryPolicy recoveryPolicy = null,
            IActionPresentationPolicy presentationPolicy = null,
            IActionExecutionPreparationPolicy executionPreparationPolicy = null)
        {
            Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
            Binder = binder;
            AdmissionPolicy = admissionPolicy;
            PlanFactory = planFactory;
            CandidateProvider = candidateProvider;
            RecoveryPolicy = recoveryPolicy;
            PresentationPolicy = presentationPolicy;
            ExecutionPreparationPolicy = executionPreparationPolicy;
        }

        public ActionDescriptor Descriptor { get; }
        public IActionCandidateProvider CandidateProvider { get; }
        public IActionBinder Binder { get; }
        public IActionAdmissionPolicy AdmissionPolicy { get; }
        public IActionPlanFactory PlanFactory { get; }
        public IActionRecoveryPolicy RecoveryPolicy { get; }
        public IActionPresentationPolicy PresentationPolicy { get; }
        public IActionExecutionPreparationPolicy ExecutionPreparationPolicy { get; }
    }

    public sealed class ExecutionAuthorization
    {
        private readonly HashSet<string> authorizedEntityIds;

        public ExecutionAuthorization(string actionId, ActionSelectionMode selectionMode,
            IEnumerable<string> authorizedEntityIds, ActionConfirmationPolicy confirmationPolicy,
            int proposalRevision, int worldRevision)
        {
            if (!ActionContractRules.IsIdentifier(actionId))
                throw new ArgumentException("Action ID must be a bounded identifier.", nameof(actionId));
            string[] entities = (authorizedEntityIds ?? Array.Empty<string>()).ToArray();
            if (entities.Length < 1 || entities.Length > 16 || entities.Any(value => !ActionContractRules.IsEntityId(value)) ||
                entities.Distinct(StringComparer.Ordinal).Count() != entities.Length)
                throw new ArgumentException("Authorized entity IDs must be bounded, valid and unique.", nameof(authorizedEntityIds));
            if (selectionMode == ActionSelectionMode.Exact && entities.Length != 1)
                throw new ArgumentException("Exact authorization requires one entity.", nameof(authorizedEntityIds));
            if (proposalRevision < 0) throw new ArgumentOutOfRangeException(nameof(proposalRevision));
            if (worldRevision < 0) throw new ArgumentOutOfRangeException(nameof(worldRevision));

            ActionId = actionId;
            SelectionMode = selectionMode;
            AuthorizedEntityIds = entities;
            this.authorizedEntityIds = new HashSet<string>(entities, StringComparer.Ordinal);
            ConfirmationPolicy = confirmationPolicy;
            ProposalRevision = proposalRevision;
            WorldRevision = worldRevision;
        }

        public string ActionId { get; }
        public ActionSelectionMode SelectionMode { get; }
        public IReadOnlyList<string> AuthorizedEntityIds { get; }
        public ActionConfirmationPolicy ConfirmationPolicy { get; }
        public int ProposalRevision { get; }
        public int WorldRevision { get; }

        public bool Authorizes(string actionId, string entityId) =>
            string.Equals(ActionId, actionId, StringComparison.Ordinal) && authorizedEntityIds.Contains(entityId);
    }

    internal static class ActionContractRules
    {
        private static readonly Regex Identifier = new("^[a-z][a-z0-9_]{0,63}$", RegexOptions.CultureInvariant);
        private static readonly Regex EntityId = new("^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant);
        private static readonly Regex Version = new("^[0-9]+\\.[0-9]+$", RegexOptions.CultureInvariant);

        public static bool IsIdentifier(string value) => !string.IsNullOrWhiteSpace(value) && Identifier.IsMatch(value);
        public static bool IsEntityId(string value) => !string.IsNullOrWhiteSpace(value) && EntityId.IsMatch(value);
        public static bool IsVersion(string value) => !string.IsNullOrWhiteSpace(value) && Version.IsMatch(value);
    }
}
