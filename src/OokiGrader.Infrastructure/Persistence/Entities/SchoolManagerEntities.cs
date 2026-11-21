namespace OokiGrader.Infrastructure.Persistence.Entities;

public sealed class GuardianMessageTemplatesEntity : IRevisionedEntity
{
    private const string Introduction =
        "いつも大変お世話になっております。\n先日実施いたしました暗記テストの結果をお知らせいたします。\n\n" +
        "教科：＜教科＞\n範囲：＜タイトル＞\n結果：＜結果＞\n\n" +
        "答案をPDFで添付しておりますので、あわせてご確認ください。";

    public string Id { get; set; } = "school-manager";
    public string Title { get; set; } = "暗記テスト採点結果のお知らせ";
    public string Pass { get; set; } = Introduction +
        "\nよく準備して臨むことができました。この調子で、次の範囲にも取り組んでいきましょう。ご家庭でもぜひ声をかけてあげてください。";
    public string Fail { get; set; } = Introduction +
        "\n次回の通塾時に、本人の都合のよいタイミングで再テストを受けることができます。それまでに該当範囲をもう一度見直しておくよう、ご家庭でもお声がけいただけますと幸いです。";
    public string Unconfigured { get; set; } = Introduction;
    public long Revision { get; set; }
}

public sealed class SchoolManagerSettingsEntity : IRevisionedEntity, IUpdatedEntity
{
    public string Id { get; set; } = "school-manager";
    public string BaseUrl { get; set; } = "https://fsm.flens.jp/";
    public string Username { get; set; } = string.Empty;
    public string? PasswordSecretReference { get; set; }
    public long CredentialRevision { get; set; }
    public bool Enabled { get; set; }
    public bool DryRun { get; set; } = true;
    public DateTimeOffset? ActivationStartedAt { get; set; }
    public DateTimeOffset? LastCredentialTestedAt { get; set; }
    public string? LastErrorCode { get; set; }
    public string? SafeErrorDetail { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; } = 1;
}

public sealed class PassFailImportEntity : IRevisionedEntity, IUpdatedEntity
{
    public string Id { get; set; } = string.Empty;
    public string SourceSha256 { get; set; } = string.Empty;
    public string SourceFileName { get; set; } = string.Empty;
    public DateTimeOffset SourceLastWriteAt { get; set; }
    public string State { get; set; } = "processing";
    public int RowCount { get; set; }
    public int DeliveryCount { get; set; }
    public string? ErrorCode { get; set; }
    public string? SafeErrorDetail { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public long Revision { get; set; } = 1;

    public ICollection<GuardianDeliveryEntity> Deliveries { get; } =
        new List<GuardianDeliveryEntity>();
}

public sealed class GuardianDeliveryEntity : IRevisionedEntity, IUpdatedEntity
{
    public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string StudentId { get; set; } = string.Empty;
    public string? SubmissionId { get; set; }
    public string? ExportRecordId { get; set; }
    public string? PassFailImportId { get; set; }
    public string? FileReferenceId { get; set; }
    public string SourceKey { get; set; } = string.Empty;
    public string? ResultSubject { get; set; }
    public string? ResultTitle { get; set; }
    public string? ResultVerdict { get; set; }
    public string AttachmentName { get; set; } = string.Empty;
    public string State { get; set; } = "pending";
    public DateTimeOffset NotBeforeAt { get; set; }
    public int AttemptCount { get; set; }
    public int MaxAttempts { get; set; } = 5;
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? LastDryRunAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateOnly? SentLocalDate { get; set; }
    public string? SchoolManagerThreadId { get; set; }
    public string? LastErrorCode { get; set; }
    public string? SafeErrorDetail { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; } = 1;

    public StudentEntity Student { get; set; } = null!;
    public SubmissionEntity? Submission { get; set; }
    public ExportRecordEntity? ExportRecord { get; set; }
    public PassFailImportEntity? PassFailImport { get; set; }
    public FileReferenceEntity? FileReference { get; set; }
}
