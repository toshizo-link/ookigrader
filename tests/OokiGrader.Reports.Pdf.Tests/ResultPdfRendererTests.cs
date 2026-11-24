using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using OokiGrader.Reports.Pdf;
using PdfSharp.Drawing;
using PdfSharp.Pdf.IO;
using PdfSharp.Pdf;

namespace OokiGrader.Reports.Pdf.Tests;

public sealed class ResultPdfRendererTests
{
    [Fact]
    public void UniformQuizUsesVerdictsAndRetainsPointsForWeightedPartialOrOverriddenScores()
    {
        var questions = new[]
        {
            Answer("1", "徳川家康", "徳川家康", "correct", 1_000),
            Answer("2", "大阪", "江戸", "incorrect", 0),
            Answer("3", "参勤交代", "参勤交代", "correct", 1_000) with { IsCorrected = true },
            Answer("4", null, "出島", "blank", 0),
            Answer("5", null, "蘭学", "unreadable", 0),
        };
        var report = CreateReport(questionCount: 0, longQuestions: false) with
        {
            SchoolName = "採点結果サンプル",
            StudentDisplayName = "見本 太郎",
            StudentNumber = null,
            TestTitle = "社会 暗記テスト（江戸時代）",
            StudentGradeLabel = "小6",
            GradedDate = new DateOnly(2026, 11, 24),
            GeneratedAt = new DateTimeOffset(2026, 11, 24, 8, 15, 0, TimeSpan.Zero),
            Questions = questions, PossiblePointsMilli = 5_000, EarnedPointsMilli = 2_000,
            IsCorrectedGrade = true, MaximumMistakesToPass = 2,
        };
        Assert.False(report.ShowQuestionPoints);
        var renderer = new ResultPdfRenderer();
        var marked = renderer.Render(report);
        WriteDiagnosticPdf("uniform-verdicts.pdf", marked.PdfBytes);
        Assert.Equal(1, marked.PageCount);
        var marks = UsedFontMappings(marked.PdfBytes);
        Assert.Contains("<25CB>", marks, StringComparison.Ordinal); // ○
        Assert.Contains("<00D7>", marks, StringComparison.Ordinal); // ×
        Assert.Contains("<FF1F>", marks, StringComparison.Ordinal); // ？

        var weighted = report with
        {
            Questions = [.. questions, Answer("6", "米", "米・塩", "partial", 500) with { MaximumPointsMilli = 2_000 }],
            EarnedPointsMilli = 2_500, PossiblePointsMilli = 7_000,
        };
        Assert.True(weighted.ShowQuestionPoints);
        var scored = renderer.Render(weighted);
        WriteDiagnosticPdf("weighted-verdicts.pdf", scored.PdfBytes);
        Assert.Equal(1, scored.PageCount);
        Assert.Contains("<25B3>", UsedFontMappings(scored.PdfBytes), StringComparison.Ordinal); // △
        Assert.True((report with { Questions = [questions[0], questions[1] with { MaximumPointsMilli = 2_000 }] }).ShowQuestionPoints);
        Assert.True((report with { Questions = [questions[0] with { Outcome = "partial", AwardedPointsMilli = 500 }] }).ShowQuestionPoints);
        Assert.True((report with { Questions = [questions[0] with { Outcome = "incorrect" }] }).ShowQuestionPoints);
        var pointsRequired = report with { AlwaysShowQuestionPoints = true };
        Assert.True(pointsRequired.ShowQuestionPoints);
        Assert.NotEqual(ResultReportSourceHasher.Compute(report), ResultReportSourceHasher.Compute(pointsRequired));

        static ResultReportQuestion Answer(string label, string? answer, string model, string outcome, long points) =>
            new(label, $"設問{label}", answer, points, 1_000, outcome, false, null, [model]);

        static string UsedFontMappings(byte[] bytes)
        {
            using var pdf = PdfReader.Open(new MemoryStream(bytes, writable: false));
            return string.Join('\n', pdf.Internals.GetAllObjects().OfType<PdfDictionary>()
                .Select(dict => dict.Elements.GetDictionary("/ToUnicode")?.Stream)
                .OfType<PdfDictionary.PdfStream>()
                .Select(stream => Encoding.ASCII.GetString(stream.UnfilteredValue)));
        }
    }

    [Fact]
    public void PassCriteriaChangesReportIdentityAndRendersInSummary()
    {
        var report = CreateReport(questionCount: 6, longQuestions: false);
        var configured = report with { MaximumMistakesToPass = 4 };
        Assert.NotEqual(ResultReportSourceHasher.Compute(report), ResultReportSourceHasher.Compute(configured));
        Assert.NotEqual(ResultReportSourceHasher.Compute(configured),
            ResultReportSourceHasher.Compute(configured with { MaximumMistakesToPass = 3 }));
        var rendered = new ResultPdfRenderer().Render(configured);
        WriteDiagnosticPdf("pass-criteria-report.pdf", rendered.PdfBytes);
        Assert.Equal(1, rendered.PageCount);
    }

    [Fact]
    public void JapaneseLongReportRendersEmbeddedFontAndMultiplePages()
    {
        var report = CreateReport(questionCount: 45, longQuestions: true);

        var rendered = new ResultPdfRenderer().Render(report);
        WriteDiagnosticPdf("japanese-result-report-sample.pdf", rendered.PdfBytes);

        Assert.True(rendered.PageCount >= 3);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(rendered.PdfBytes))
                .ToLowerInvariant(),
            rendered.Sha256);
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(rendered.PdfBytes, 0, 5));
        using var parsed = PdfReader.Open(
            new MemoryStream(rendered.PdfBytes, writable: false));
        Assert.Equal(rendered.PageCount, parsed.PageCount);

        var pdfSyntax = Encoding.Latin1.GetString(rendered.PdfBytes);
        Assert.Contains("/FontFile2", pdfSyntax, StringComparison.Ordinal);
        Assert.Contains("/ToUnicode", pdfSyntax, StringComparison.Ordinal);
    }

    [Fact]
    public void SameReportProducesSameVerifiedBytes()
    {
        var report = CreateReport(questionCount: 3, longQuestions: false);
        var renderer = new ResultPdfRenderer();

        var first = renderer.Render(report);
        var second = renderer.Render(report);
        WriteDiagnosticPdf("deterministic-first.pdf", first.PdfBytes);
        WriteDiagnosticPdf("deterministic-second.pdf", second.PdfBytes);

        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(first.PdfBytes, second.PdfBytes);
    }

    [Fact]
    public void ZeroQuestionReportRendersOnePage()
    {
        var report = CreateReport(questionCount: 0, longQuestions: false);

        var rendered = new ResultPdfRenderer().Render(report);

        Assert.Equal(1, rendered.PageCount);
    }

    [Fact]
    public void AllTranscriptPagesPrecedeTheOriginalAnswerPagesInScanOrder()
    {
        var report = CreateReport(questionCount: 45, longQuestions: true) with
        {
            OriginalScanSha256 = new string('a', 64),
        };
        var transcript = new ResultPdfRenderer().Render(report);
        using var originalDocument = new PdfDocument();
        originalDocument.AddPage().Width = XUnit.FromPoint(280);
        originalDocument.AddPage().Width = XUnit.FromPoint(310);
        using var originalBytes = new MemoryStream();
        originalDocument.Save(originalBytes, closeStream: false);
        originalBytes.Position = 0;

        var combined = ResultPdfComposer.AppendOriginal(
            originalBytes,
            transcript,
            report);

        Assert.Equal(transcript.PageCount + 2, combined.PageCount);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(combined.PdfBytes))
                .ToLowerInvariant(),
            combined.Sha256);
        using var parsed = PdfReader.Open(
            new MemoryStream(combined.PdfBytes, writable: false));
        using var parsedTranscript = PdfReader.Open(
            new MemoryStream(transcript.PdfBytes, writable: false));
        Assert.True(parsedTranscript.PageCount > 1);
        for (var page = 0; page < parsedTranscript.PageCount; page++)
        {
            Assert.Equal(parsedTranscript.Pages[page].Contents.CreateSingleContent().Stream.Value,
                parsed.Pages[page].Contents.CreateSingleContent().Stream.Value);
        }
        Assert.Equal(280, parsed.Pages[transcript.PageCount].Width.Point);
        Assert.Equal(310, parsed.Pages[transcript.PageCount + 1].Width.Point);
        WriteDiagnosticPdf("transcript-before-originals.pdf", combined.PdfBytes);
    }

    [Fact]
    public void SourceHashChangesForCorrectionButNotReportIdentifier()
    {
        var report = CreateReport(questionCount: 1, longQuestions: false);
        var sameSourceNewExport = report with { ReportId = "01NEWEXPORT" };
        var correction = report with
        {
            EarnedPointsMilli = 500,
            IsCorrectedGrade = true,
            Questions =
            [
                report.Questions[0] with
                {
                    AwardedPointsMilli = 500,
                    Outcome = "partial",
                    IsCorrected = true,
                },
            ],
        };

        Assert.Equal(
            ResultReportSourceHasher.Compute(report),
            ResultReportSourceHasher.Compute(sameSourceNewExport));
        Assert.NotEqual(
            ResultReportSourceHasher.Compute(report),
            ResultReportSourceHasher.Compute(correction));
        Assert.NotEqual(ResultReportSourceHasher.Compute(report),
            ResultReportSourceHasher.Compute(report with { GradedDate = report.GradedDate.AddDays(1) }));
        Assert.NotEqual(
            ResultReportSourceHasher.Compute(report),
            ResultReportSourceHasher.Compute(report with
            {
                StudentClassLabel = "6年A組",
            }));
        Assert.NotEqual(
            ResultReportSourceHasher.Compute(report),
            ResultReportSourceHasher.Compute(report with
            {
                Questions =
                [
                    report.Questions[0] with
                    {
                        ModelAnswers = ["おおき", "たいぼく"],
                    },
                ],
            }));
    }

    [Fact]
    public void RendererRejectsTotalsThatDoNotMatchQuestions()
    {
        var report = CreateReport(questionCount: 1, longQuestions: false) with
        {
            EarnedPointsMilli = 0,
        };

        var error = Assert.Throws<ArgumentException>(
            () => new ResultPdfRenderer().Render(report));

        Assert.Contains("totals", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ResultReportDocument CreateReport(
        int questionCount,
        bool longQuestions)
    {
        var questions = Enumerable.Range(1, questionCount)
            .Select(index => new ResultReportQuestion(
                index.ToString(CultureInfo.InvariantCulture),
                longQuestions
                    ? "次の文章を読んで、登場人物の気持ちの変化を本文中の表現に即して説明しなさい。" +
                        string.Concat(Enumerable.Repeat(
                            "これは日本語の長い設問が列幅に合わせて正しく折り返されることを確認する文章です。",
                            2))
                    : $"漢字「大木」の読みを答えなさい（{index}）。",
                index % 7 == 0
                    ? null
                    : "おおき。理由を含む少し長い手書き解答の認識結果です。",
                1_000,
                1_000,
                index % 7 == 0 ? "blank" : "correct",
                index == 2,
                index == 2 ? "教師が表記を確認し、現在の得点へ訂正しました。" : null))
            .ToArray();
        return new ResultReportDocument(
            "01JREPORT000000000000000001",
            "大木学習塾",
            "大木 花子",
            "S-0042",
            "国語・漢字確認テスト 第4回",
            new DateOnly(2026, 7, 27),
            new DateOnly(2026, 7, 30),
            3,
            7,
            questions.Sum(item => item.AwardedPointsMilli),
            questions.Sum(item => item.MaximumPointsMilli),
            questions,
            new DateTimeOffset(2026, 8, 5, 8, 15, 0, TimeSpan.Zero),
            IsCorrectedGrade: questions.Any(item => item.IsCorrected));
    }

    private static void WriteDiagnosticPdf(string filename, byte[] bytes)
    {
        var output = Environment.GetEnvironmentVariable("OOKI_REPORT_TEST_OUTPUT");
        if (string.IsNullOrWhiteSpace(output))
        {
            return;
        }

        Directory.CreateDirectory(output);
        File.WriteAllBytes(Path.Combine(output, filename), bytes);
    }
}
