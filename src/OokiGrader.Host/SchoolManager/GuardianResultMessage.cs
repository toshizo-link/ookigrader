using System.Text.RegularExpressions;
using OokiGrader.Domain.Grading;
using OokiGrader.Host.Reports;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Host.SchoolManager;

internal sealed record GuardianMessage(string Title, string Body);

internal static partial class GuardianResultMessage
{
    [GeneratedRegex("＜(?:教科|タイトル|結果)＞")]
    private static partial Regex Tokens();

    internal static void SetSnapshot(GuardianDeliveryEntity delivery, ResultReportSource source)
    {
        delivery.ResultSubject = source.Subject;
        delivery.ResultTitle = source.Document.TestTitle;
        delivery.ResultVerdict = MistakePassCriteria.Evaluate(source.Document.MaximumMistakesToPass,
            source.Document.Questions.Select(question => question.Outcome));
    }

    internal static GuardianMessage Format(GuardianDeliveryEntity delivery, GuardianMessageTemplatesEntity templates)
    {
        var (label, body) = delivery.ResultVerdict switch
        {
            "pass" => ("合格", templates.Pass),
            "fail" => ("不合格", templates.Fail),
            "unconfigured" => ("合否基準未設定", templates.Unconfigured),
            _ => throw new InvalidOperationException("The result message has no verified verdict."),
        };
        var subject = string.IsNullOrWhiteSpace(delivery.ResultSubject) ? "未設定" : delivery.ResultSubject;
        var title = string.IsNullOrWhiteSpace(delivery.ResultTitle) ? "未設定" : delivery.ResultTitle;
        string Render(string template) => Tokens().Replace(template, match => match.Value switch
        {
            "＜教科＞" => subject,
            "＜タイトル＞" => title,
            _ => label,
        });
        return new GuardianMessage(Render(templates.Title), Render(body));
    }
}
