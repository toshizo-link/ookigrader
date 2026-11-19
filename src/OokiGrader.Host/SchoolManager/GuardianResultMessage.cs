using OokiGrader.Domain.Grading;
using OokiGrader.Host.Reports;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Host.SchoolManager;

internal static class GuardianResultMessage
{
    internal static void SetSnapshot(GuardianDeliveryEntity delivery, ResultReportSource source)
    {
        delivery.ResultSubject = source.Subject;
        delivery.ResultTitle = source.Document.TestTitle;
        delivery.ResultVerdict = MistakePassCriteria.Evaluate(source.Document.MaximumMistakesToPass,
            source.Document.Questions.Select(question => question.Outcome));
    }

    internal static string Format(GuardianDeliveryEntity delivery, GuardianMessageTemplatesEntity templates)
    {
        var (label, paragraph) = delivery.ResultVerdict switch
        {
            "pass" => ("合格", templates.Pass),
            "fail" => ("不合格", templates.Fail),
            "unconfigured" => ("合否基準未設定", templates.Unconfigured),
            _ => throw new InvalidOperationException("The result message has no verified verdict."),
        };
        var subject = string.IsNullOrWhiteSpace(delivery.ResultSubject) ? "未設定" : delivery.ResultSubject;
        var header = $"教科　{subject}\nテスト名　{delivery.ResultTitle}\n判定　{label}";
        return paragraph.Length == 0 ? header : $"{header}\n\n{paragraph}";
    }
}
