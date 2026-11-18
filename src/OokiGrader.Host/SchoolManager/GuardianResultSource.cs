using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Host.SchoolManager;

internal sealed class GuardianResultSource
{
    public required SubmissionEntity Submission { get; init; }
    public required string SourceKey { get; init; }

    public static IQueryable<GuardianResultSource> Current(OokiGraderDbContext db) =>
        db.GradingRuns
            .Where(run => run.Id == run.Submission.CurrentGradingRunId
                && run.State == "finalized"
                && run.Submission.State == "finalized"
                && run.Submission.FinalizedAt != null
                && run.Submission.VoidedAt == null
                && run.Submission.AssignedStudentId != null)
            .Select(run => new GuardianResultSource
            {
                Submission = run.Submission,
                SourceKey = run.SubmissionId + ":" + run.Id + ":" + run.ResultSourceRevision,
            });

    public static IQueryable<GuardianDeliveryEntity> ObsoleteDeliveries(OokiGraderDbContext db)
    {
        var current = Current(db);
        return db.GuardianDeliveries.Where(delivery => delivery.Kind == "result_pdf"
            && delivery.State != "sent"
            && delivery.State != "canceled"
            && delivery.State != "sending"
            && delivery.LastErrorCode != "message_send_outcome_unknown"
            && !current.Any(source =>
                source.Submission.Id == delivery.SubmissionId
                && source.Submission.AssignedStudentId == delivery.StudentId
                && source.SourceKey == delivery.SourceKey));
    }

    public static void Cancel(GuardianDeliveryEntity delivery, DateTimeOffset now)
    {
        delivery.State = "canceled";
        delivery.LastErrorCode = "result_reopened";
        delivery.SafeErrorDetail = "The finalized result or its assigned student changed before guardian delivery.";
        delivery.UpdatedAt = now;
    }
}
