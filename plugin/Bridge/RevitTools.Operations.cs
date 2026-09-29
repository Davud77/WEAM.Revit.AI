using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WEAM.Revit.AI;

internal static partial class RevitTools
{
    private sealed class Revision { public long Value; }
    // Revit can return different managed wrappers for the same open document.
    // Document.Equals/GetHashCode identify the native document session; reference identity does not.
    private static readonly Dictionary<Document, Revision> Revisions = new();
    private static readonly Dictionary<string, OperationPlan> OperationPlans = new();
    // Revit API execution and DocumentChanged callbacks both run on the UI thread.
    private static int SimulationDepth;
    private static readonly ConditionalWeakTable<Transaction, OperationFailures> TransactionFailures = new();
    private static readonly List<string> LegacyWarnings = [];
    private static HashSet<long>? CapturedDeletionIds;

    private static void ValidateLegacyPlan(ParameterPlan plan, Document document)
    {
        GetPlan(plan.Id, plan.Kind);
        if (plan.OwnerDocument?.IsValidObject != true || !plan.OwnerDocument.Equals(document))
            throw new InvalidOperationException("This plan belongs to another Revit document.");
        if (plan.DocumentRevision != DocumentRevision(document))
            throw new InvalidOperationException("The document changed after preview. Prepare a new preview.");
    }

    private static void BeginCheckedTransaction(Transaction transaction)
    {
        if (transaction.Start() != TransactionStatus.Started) throw new InvalidOperationException("Cannot start Revit transaction.");
        var failures = new OperationFailures();
        TransactionFailures.Add(transaction, failures);
        transaction.SetFailureHandlingOptions(transaction.GetFailureHandlingOptions()
            .SetFailuresPreprocessor(failures).SetClearAfterRollback(true).SetForcedModalHandling(true));
    }

    private static void CommitCheckedTransaction(Transaction transaction, Func<bool>? isCancelled)
    {
        if (isCancelled?.Invoke() == true) throw new OperationCanceledException("MCP request cancelled before commit.");
        var status = transaction.Commit();
        TransactionFailures.TryGetValue(transaction, out var failures);
        if (status != TransactionStatus.Committed)
            throw new InvalidOperationException("Revit did not commit the transaction. " + string.Join("; ", failures?.Errors ?? []));
        if (failures is not null) LegacyWarnings.AddRange(failures.Warnings);
    }
    private sealed record OperationPlan(string Id, string Kind, Document Document, long Revision,
        DateTime CreatedUtc, JsonElement Arguments, string Summary, JsonElement Preview);

    internal static void OnDocumentChanged(object? sender, DocumentChangedEventArgs args)
    {
        CapturedDeletionIds?.UnionWith(args.GetDeletedElementIds().Select(id => id.Value));
        if (SimulationDepth == 0) GetRevision(args.GetDocument()).Value++;
    }

    private static Revision GetRevision(Document document)
    {
        foreach (var stale in Revisions.Keys.Where(doc => !doc.IsValidObject).ToArray()) Revisions.Remove(stale);
        if (!Revisions.TryGetValue(document, out var revision)) Revisions[document] = revision = new Revision();
        return revision;
    }

    private static long DocumentRevision(Document document) => GetRevision(document).Value;

    private static JsonElement BindActiveView(UIApplication app, JsonElement args)
    {
        // Never let a plan silently target a newly activated view during apply.
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(args.GetRawText())!;
        if (!values.ContainsKey("viewId"))
            values["viewId"] = JsonSerializer.SerializeToElement(app.ActiveUIDocument!.ActiveView.Id.Value);
        return JsonSerializer.SerializeToElement(values);
    }

    private static object PreviewOperation(UIApplication app, JsonElement args, string kind, string summary,
        Func<Document, JsonElement, object> operation)
    {
        var document = RequireDocument(app);
        RequireWritableProject(document);
        var boundArgs = BindActiveView(app, args);
        var preview = SimulateOperation(document, boundArgs, summary, operation);
        PruneOperationPlans();
        if (OperationPlans.Count >= 100) throw new InvalidOperationException("Too many pending previews. Apply a plan or wait for expiry.");
        var id = Guid.NewGuid().ToString("N");
        OperationPlans[id] = new OperationPlan(id, kind, document, DocumentRevision(document),
            DateTime.UtcNow, boundArgs.Clone(), summary, preview);
        return new { planCreated = true, planId = id, expiresInMinutes = 10,
            previewMode = "transaction_commit_then_group_rollback", modelChanged = false,
            note = "Предпросмотр проверен Revit и полностью отменён. ID вновь созданных объектов в preview временные; используйте ID из apply.",
            preview };
    }

    private static JsonElement SimulateOperation(Document document, JsonElement args, string summary,
        Func<Document, JsonElement, object> operation)
    {
        SimulationDepth++;
        try
        {
            using var group = new TransactionGroup(document, "WEAM AI: предпросмотр");
            if (group.Start() != TransactionStatus.Started) throw new InvalidOperationException("Cannot start preview group.");
            try
            {
                var result = ExecuteOperationTransaction(document, args, summary, operation, null);
                if (group.RollBack() != TransactionStatus.RolledBack) throw new InvalidOperationException("Revit did not roll back the preview group.");
                return result;
            }
            finally { if (group.GetStatus() == TransactionStatus.Started) group.RollBack(); }
        }
        finally { SimulationDepth--; }
    }

    private static object ApplyOperation(UIApplication app, JsonElement args, string kind, string summary,
        Func<Document, JsonElement, object> operation, Func<bool>? isCancelled)
    {
        var document = RequireDocument(app);
        RequireWritableProject(document);
        var plan = GetOperationPlan(document, JsonTools.GetString(args, "planId"), kind);
        // Deleting dependencies must be explicitly requested, independently of the Yes dialog.
        if (kind == "delete_checked")
        {
            var dependencies = plan.Preview.GetProperty("result").GetProperty("dependentCount").GetInt32();
            var allowed = args.TryGetProperty("allowDependentDeletion", out var flag) && flag.ValueKind == JsonValueKind.True;
            if (dependencies > 0 && !allowed)
                throw new InvalidOperationException($"Deletion includes {dependencies} dependent elements. Review the preview and explicitly pass allowDependentDeletion=true.");
        }
        var details = plan.Preview.GetRawText();
        var dialog = new TaskDialog("WEAM.Revit.AI")
        {
            MainInstruction = summary,
            MainContent = $"Проект: {document.Title}\n\nПроверенный план: {plan.Id}\nИзменения применяются одной транзакцией Revit.",
            ExpandedContent = details.Length <= 24000 ? details : details[..24000] + "\n… полный результат доступен в MCP-предпросмотре",
            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
            DefaultButton = TaskDialogResult.No
        };
        if (isCancelled?.Invoke() == true || !ConfirmOperation(dialog) || isCancelled?.Invoke() == true)
            return new { applied = false, cancelled = true };
        GetOperationPlan(document, plan.Id, kind);
        using var group = new TransactionGroup(document, "WEAM AI: подтверждённая операция");
        if (group.Start() != TransactionStatus.Started) throw new InvalidOperationException("Cannot start apply group.");
        try
        {
            var result = ExecuteOperationTransaction(document, plan.Arguments, summary, operation, isCancelled);
            if (kind == "delete_checked")
            {
                var expected = plan.Preview.GetProperty("result").GetProperty("deletedIds").EnumerateArray().Select(x => x.GetInt64()).Order();
                var actual = result.GetProperty("result").GetProperty("deletedIds").EnumerateArray().Select(x => x.GetInt64()).Order();
                if (!expected.SequenceEqual(actual))
                    throw new InvalidOperationException("Deletion dependencies changed after preview, including commit-time changes. Nothing was deleted; prepare a new preview.");
            }
            if (isCancelled?.Invoke() == true) throw new OperationCanceledException("MCP request cancelled before final commit.");
            if (group.Assimilate() != TransactionStatus.Committed) throw new InvalidOperationException("Revit did not commit the operation group.");
            OperationPlans.Remove(plan.Id);
            return new { applied = true, result = result.GetProperty("result"), warnings = result.GetProperty("warnings") };
        }
        finally { if (group.GetStatus() == TransactionStatus.Started) group.RollBack(); }
    }

    private static OperationPlan GetOperationPlan(Document document, string id, string kind)
    {
        PruneOperationPlans();
        if (!OperationPlans.TryGetValue(id, out var plan) || plan.Kind != kind)
            throw new InvalidOperationException("Plan not found, expired or already applied. Prepare a new preview.");
        if (!plan.Document.Equals(document)) throw new InvalidOperationException("This plan belongs to another Revit document.");
        if (DocumentRevision(document) != plan.Revision)
            throw new InvalidOperationException("The document changed after the preview. Prepare a new preview.");
        return plan;
    }

    private static void PruneOperationPlans()
    {
        foreach (var entry in OperationPlans.ToArray())
            if (!entry.Value.Document.IsValidObject || DateTime.UtcNow - entry.Value.CreatedUtc > PlanLifetime)
                OperationPlans.Remove(entry.Key);
    }

    private static void RequireWritableProject(Document document)
    {
        if (document.IsFamilyDocument || document.IsReadOnly || document.IsModifiable)
            throw new InvalidOperationException("A writable project document without another open transaction is required.");
    }

    private static JsonElement ExecuteOperationTransaction(Document document, JsonElement args, string summary,
        Func<Document, JsonElement, object> operation, Func<bool>? isCancelled)
    {
        using var transaction = new Transaction(document, "WEAM AI: " + summary);
        if (transaction.Start() != TransactionStatus.Started) throw new InvalidOperationException("Cannot start Revit transaction.");
        var failures = new OperationFailures();
        transaction.SetFailureHandlingOptions(transaction.GetFailureHandlingOptions()
            .SetFailuresPreprocessor(failures).SetClearAfterRollback(true).SetForcedModalHandling(true));
        var previousCapture = CapturedDeletionIds;
        var deletionCapture = new HashSet<long>();
        CapturedDeletionIds = deletionCapture;
        try
        {
            if (isCancelled?.Invoke() == true) throw new OperationCanceledException("MCP request cancelled.");
            var result = operation(document, args);
            document.Regenerate();
            // Materialize all DTOs before rollback can invalidate Revit objects.
            var serialized = JsonSerializer.SerializeToElement(result, JsonTools.SerializerOptions);
            if (isCancelled?.Invoke() == true) throw new OperationCanceledException("MCP request cancelled before commit.");
            if (transaction.Commit() != TransactionStatus.Committed)
                throw new InvalidOperationException("Revit rolled back the operation. " + string.Join("; ", failures.Errors));
            if (serialized.ValueKind == JsonValueKind.Object && serialized.TryGetProperty("deletedIds", out var immediateDeleted))
            {
                // DocumentChanged includes additional deletes performed by Revit updaters during commit.
                deletionCapture.UnionWith(immediateDeleted.EnumerateArray().Select(value => value.GetInt64()));
                var requested = serialized.GetProperty("requestedIds").EnumerateArray().Select(value => value.GetInt64()).ToHashSet();
                var dependent = deletionCapture.Where(id => !requested.Contains(id)).Order().ToArray();
                var row = JsonNode.Parse(serialized.GetRawText())!.AsObject();
                row["deletedIds"] = JsonSerializer.SerializeToNode(deletionCapture.Order().ToArray());
                row["deletedCount"] = deletionCapture.Count;
                row["dependentIds"] = JsonSerializer.SerializeToNode(dependent);
                row["dependentCount"] = dependent.Length;
                row["requiresDependentDeletionPermission"] = dependent.Length > 0;
                serialized = JsonSerializer.SerializeToElement(row);
            }
            return JsonSerializer.SerializeToElement(new { result = serialized, warnings = failures.Warnings }, JsonTools.SerializerOptions);
        }
        catch
        {
            if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
            throw;
        }
        finally { CapturedDeletionIds = previousCapture; }
    }

    private sealed class OperationFailures : IFailuresPreprocessor
    {
        public List<string> Warnings { get; } = [];
        public List<string> Errors { get; } = [];
        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            foreach (var failure in accessor.GetFailureMessages())
            {
                if (failure.GetSeverity() == FailureSeverity.Warning)
                {
                    Warnings.Add(failure.GetDescriptionText());
                    accessor.DeleteWarning(failure);
                }
                else Errors.Add(failure.GetDescriptionText());
            }
            return Errors.Count > 0 ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
        }
    }
}
