namespace OokiGrader.Infrastructure.Persistence.Entities;

public sealed class GuardianMessageTemplatesEntity : IRevisionedEntity
{
    public string Id { get; set; } = "school-manager";
    public string Pass { get; set; } = "合格おめでとうございます。詳しい採点結果は添付のPDFをご覧ください。";
    public string Fail { get; set; } = "添付のPDFで間違えた問題を確認し、次のテストに向けて復習しましょう。";
    public string Unconfigured { get; set; } = "採点結果をお送りします。添付のPDFをご確認ください。";
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
