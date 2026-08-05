using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;

namespace Hades.Server.Mcp;

/// <summary>Declares which op(s), within a *_apply/*_manage tool's flat XxxOperation record, an
/// optional field belongs to - e.g. MaterialApplyOperation's 'sourcePath' is
/// <c>[OpField("duplicate")]</c> only, even though the record itself is one flat shape shared by
/// all five of material_apply's ops (see SceneApplyOperation's own class doc comment,
/// SceneApplyTool.cs, for why the records are flat rather than a discriminated union). Applied
/// directly beside the field's own [JsonPropertyName] - the SAME place its op-grouping "//" comment
/// already lives - so OperationFieldValidator's per-op table can never point at different ops than a
/// hand-copied second list could silently drift toward. See OperationFieldValidator's own doc
/// comment for how this drives "unknown field" rejection.</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
internal sealed class OpFieldAttribute : Attribute
{
    public IReadOnlyList<string> Ops { get; }
    public OpFieldAttribute(params string[] ops) => Ops = ops;
}

/// <summary>Implemented by every *_apply/*_manage tool's own flat XxxOperation record - the two
/// members <see cref="OperationFieldValidator"/> needs at the INSTANCE level (which op this one
/// operation is, and whatever System.Text.Json could not bind to any declared property at all).
/// <see cref="ExtensionData"/> is the backing store [JsonExtensionData] must be applied to on each
/// implementing record - see OperationFieldValidator's own doc comment for why this, rather than
/// letting System.Text.Json's default UnmappedMemberHandling.Skip silently discard the field, is
/// what makes an unrecognised field name catchable at all.</summary>
internal interface IBatchOperation
{
    string Op { get; }
    Dictionary<string, JsonElement>? ExtensionData { get; }
}

/// <summary>
/// Rejects an unrecognised FIELD name inside any *_apply/*_manage operation, before any wire call -
/// the inbound counterpart to <c>CommandTable.RejectUnknownParams</c> (Plugin~), which already
/// refuses an unrecognised PARAMETER name for lease.* the same way, for the same reason (its own doc
/// comment: "a mistake that needs diligence to detect is a mistake the protocol should refuse
/// outright" - added after a typo'd 'ttlMs' silently took ReloadGate's 30s default instead of the
/// 120s a caller actually asked for, costing a real misdiagnosis). SceneApplyTool/etc. already refuse
/// an unrecognised OP name the identical "whole call, zero wire calls" way (see SceneApplyTool's own
/// class doc comment, "Unknown op: refused, not ignored") - this is the missing sibling check for an
/// unrecognised FIELD name on an otherwise-valid op.
///
/// <para><b>The live reproduction this closes.</b> <c>material_apply {"op":"setProperty","path":
/// "...","property":"_Metallic","value":0.5}</c> - 'property' is not a field MaterialApplyOperation
/// declares at all (only 'propertyName' is); System.Text.Json's default UnmappedMemberHandling.Skip
/// silently dropped it, the call proceeded with no 'propertyName' on the wire, and the PLUGIN
/// reported "'material.set_property' requires a non-empty string 'propertyName' parameter" - an
/// error that reads as an app-to-plugin mapping bug when it was actually a caller typo the app should
/// have refused by name. This mechanism turns that into an immediate, local "unknown field
/// 'property'. Fields 'setProperty' accepts: op, path, propertyName, value." rejection instead.</para>
///
/// <para><b>Per-op, not per-tool.</b> Every *_apply/*_manage operation record is ONE flat shape
/// shared by every op the tool accepts (see SceneApplyOperation's own class doc comment for why) -
/// so a field name that belongs to a DIFFERENT op in the SAME tool (material_apply's 'sourcePath',
/// duplicate-only) is just as much a caller mistake as a field that exists nowhere in the record at
/// all, and gets the identical rejection. Accepting any field some other op happens to declare would
/// miss exactly the typo class this exists to catch - e.g. a 'sourcePath' left over from a duplicate
/// op copy-pasted earlier in the same spec, silently ignored on a setProperty op instead of refused.
/// Note the record's own "//" comments group fields by READABILITY, not by a strict per-op union -
/// scene_apply's 'type' sits in the same comment block as 'target'/'component' for addComponent /
/// removeComponent / setProperties / setReference / ... / select, but only addComponent/
/// removeComponent actually accept 'type' (see SceneApplyTool's own [Description] parameter text,
/// the authoritative per-op field list this table's [OpField] tags are drawn from) - which is exactly
/// why the comment grouping itself cannot drive this table.</para>
///
/// <para><b>Two distinct ways a field can be "unknown", both caught here, identically.</b>
/// (1) A JSON member that does not match ANY property System.Text.Json knows about for this record
/// type at all - caught via <see cref="IBatchOperation.ExtensionData"/>, [JsonExtensionData]'s own
/// capture bucket for exactly this case (the live reproduction above: 'property' is not a
/// MaterialApplyOperation member at all). (2) A JSON member that DOES match a declared property - so
/// System.Text.Json bound it normally - but that property's own [OpField] tags do not include THIS
/// operation's 'op' value (material_apply's 'sourcePath' sent alongside 'setProperty' instead of
/// 'duplicate' - 'sourcePath' IS a MaterialApplyOperation member, just not setProperty's). Both
/// produce the identical error shape; a caller cannot tell which case they hit and does not need to.
/// </para>
///
/// <para><b>The accepted-fields table is derived, not hand-copied.</b> The nested <c>Table&lt;TOp&gt;</c>
/// reflects on TOp's own declared properties once per type (cached in a generic static field, not
/// rebuilt per call) and reads each one's [OpField] tags directly - there is no second, parallel
/// "op -&gt; fields" list a future field addition could forget to update, and forgetting the
/// [OpField] tag itself is not a silent gap either: <c>Table&lt;TOp&gt;</c>'s static constructor
/// throws for any property that has [JsonPropertyName] but no [OpField], the moment anything first
/// touches that TOp - which for these seven tools means the first test that exercises them, not a
/// live Editor session. This is the same "fail loud, not silent" instinct
/// PluginRequiredFields.cs's own doc comment describes (App~/tests) - but that mechanism and this one
/// are deliberately separate, not shared code: PluginRequiredFields mines the PLUGIN's own source
/// (Plugin~/Assets/Hades/Tools/*.cs, via regex over JsonParams.RequireString/RequireInt calls) for
/// what it REQUIRES on the way OUT to the wire; this reflects the APP's own record types (via
/// [OpField] attributes) for what it ACCEPTS on the way IN from the caller. Different inputs
/// (plugin source text vs. app record metadata), different directions (outbound required-ness vs.
/// inbound unknown-ness), different failure shapes (a per-op field a caller forgot vs. a per-op field
/// a caller mistakenly added) - forcing them through one shared abstraction would only entangle two
/// independently-changing concerns. Both exist for the identical underlying reason, though: a
/// hand-copied second list is exactly the kind of staleness that caused the live reproduction in the
/// first place.</para>
/// </summary>
internal static class OperationFieldValidator
{
    public static void RejectUnknownFields<TOp>(string toolName, IReadOnlyList<TOp> operations) where TOp : IBatchOperation
    {
        for (var i = 0; i < operations.Count; i++)
        {
            var operation = operations[i];

            var accepted = Table<TOp>.Accepted.TryGetValue(operation.Op, out var a) ? a : Table<TOp>.OpOnly;

            // Case 1: a JSON member System.Text.Json could not map onto ANY property at all - the
            // live reproduction (e.g. 'property' where only 'propertyName' exists).
            if (operation.ExtensionData is { Count: > 0 } extra)
                Reject(toolName, i, operation.Op, extra.Keys.First(), accepted);

            // Case 2: a JSON member that IS a property of this record, bound normally, but declared
            // for a DIFFERENT op (e.g. material_apply's 'sourcePath' sent with 'setProperty'). An op
            // with no fields of its own at all (asset_manage's 'refresh') falls back to Table<TOp>.
            // AllFields here, so every other op's field is correctly treated as "not mine".
            var otherFields = Table<TOp>.OtherFields.TryGetValue(operation.Op, out var o) ? o : Table<TOp>.AllFields;
            foreach (var (jsonName, getValue) in otherFields)
            {
                if (getValue(operation) is not null)
                    Reject(toolName, i, operation.Op, jsonName, accepted);
            }
        }
    }

    static void Reject(string toolName, int index, string op, string field, IReadOnlyList<string> accepted) =>
        throw new McpException(
            $"{toolName} operations[{index}] (op '{op}'): unknown field '{field}'. Fields '{op}' accepts: {string.Join(", ", accepted)}.");

    /// <summary>Per-TOp reflection cache, built once (generic static fields are initialised once per
    /// closed generic type, not per call): which fields each op accepts (for the rejection message),
    /// and - split out per op - which fields OTHER ops declare, so a value present in one of THOSE is
    /// exactly the "sibling op's field" violation.</summary>
    static class Table<TOp>
    {
        public static readonly IReadOnlyList<string> OpOnly = ["op"];
        public static readonly IReadOnlyList<(string JsonName, Func<TOp, object?> GetValue)> AllFields;
        public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Accepted;
        public static readonly IReadOnlyDictionary<string, IReadOnlyList<(string JsonName, Func<TOp, object?> GetValue)>> OtherFields;

        static Table()
        {
            var declared = new List<(string JsonName, Func<TOp, object?> GetValue, IReadOnlyList<string> Ops)>();

            foreach (var property in typeof(TOp).GetProperties())
            {
                var jsonName = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name;
                if (jsonName is null || jsonName == "op") continue; // 'op' itself is implicitly accepted everywhere.
                if (property.GetCustomAttribute<JsonExtensionDataAttribute>() is not null) continue;

                var opField = property.GetCustomAttribute<OpFieldAttribute>() ?? throw new InvalidOperationException(
                    $"{typeof(TOp).Name}.{property.Name} has [JsonPropertyName(\"{jsonName}\")] but no [OpField(...)] - "
                    + "every optional field on a batch operation record must declare which op(s) it belongs to, so "
                    + "OperationFieldValidator can tell a caller's typo from a sibling op's own field.");

                declared.Add((jsonName, MakeGetter(property), opField.Ops));
            }

            AllFields = declared.Select(f => (f.JsonName, f.GetValue)).ToArray();

            var accepted = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            var other = new Dictionary<string, IReadOnlyList<(string, Func<TOp, object?>)>>(StringComparer.Ordinal);
            foreach (var op in declared.SelectMany(f => f.Ops).Distinct(StringComparer.Ordinal))
            {
                accepted[op] = new[] { "op" }.Concat(declared.Where(f => f.Ops.Contains(op)).Select(f => f.JsonName)).ToArray();
                other[op] = declared.Where(f => !f.Ops.Contains(op)).Select(f => (f.JsonName, f.GetValue)).ToArray();
            }
            Accepted = accepted;
            OtherFields = other;
        }

        static Func<TOp, object?> MakeGetter(PropertyInfo property) => operation => property.GetValue(operation);
    }
}
