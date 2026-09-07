using System.Text.Json;
using Dms.Domain.Common;
using Dms.Domain.Enums;

namespace Dms.Domain.Entities;

/// <summary>
/// One status's stamp: the text printed across every page of a rendered PDF, and its colour.
/// </summary>
/// <param name="Status">Which status this applies to. Fixed by the enum, never editable.</param>
/// <param name="Text">
/// Supports the same token syntax as notification rules — <c>{DocumentNumber}</c>,
/// <c>{Revision}</c>, <c>{EffectiveDate}</c>, <c>{Status}</c> — validated on save so a typo
/// surfaces in the admin screen rather than on a printed page.
/// </param>
/// <param name="ColourHex">Six hex digits, no leading hash.</param>
/// <param name="IsEnabled">
/// False prints nothing. Cannot be set false for a do-not-use status; see
/// <see cref="DocumentStatusStamps.RequiresWarning"/>.
/// </param>
public sealed record StatusStamp(
    DocumentStatus Status,
    string Text,
    string ColourHex,
    bool IsEnabled);

/// <summary>
/// The organisation's page-stamp wording, held as editable master data.
/// <para>
/// Configurable for the same reason numbering patterns, workflows and notification rules are:
/// the exact wording is dictated by a company's own SOP, and changing it should not require a
/// redeployment of a validated system.
/// </para>
/// <para>
/// Stored as a single row with the seven stamps as JSON rather than a table with seven rows.
/// The set is fixed by the <see cref="DocumentStatus"/> enum — there can never be an eighth —
/// so a table would buy nothing that a JSON column doesn't, while costing a schema migration.
/// Same shape as <see cref="PasswordPolicy"/>, and the same reasoning.
/// </para>
/// <para>
/// Note the deliberate difference from <see cref="Dms.Domain.Services.ControlledCopyWatermark"/>,
/// which is hardcoded: that watermark is composed once and stored on the PrintEvent, so a page
/// recovered years later reconciles against what the system says was printed that day. This
/// stamp is rendered fresh each time a PDF is produced, because it must reflect the document's
/// status <i>now</i> — a PDF rendered while Effective and read after supersession would
/// otherwise still claim to be current.
/// </para>
/// </summary>
public class DocumentStatusStamps : Entity
{
    private DocumentStatusStamps() { }

    private DocumentStatusStamps(string createdBy)
    {
        StampsJson = JsonSerializer.Serialize(Defaults());
        UpdatedBy = createdBy;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Serialised <see cref="StatusStamp"/> list. Exposed as a string because EF maps it to a
    /// jsonb column; callers use <see cref="Stamps"/> instead.
    /// </summary>
    public string StampsJson { get; private set; } = "";

    public string UpdatedBy { get; private set; } = "";
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// The configured stamps, with any status missing from storage filled from the defaults.
    /// <para>
    /// Filling gaps matters: a stored payload written before a new status existed would
    /// otherwise leave that status silently unstamped, which for a do-not-use status means a
    /// withdrawn document printing as though it were ordinary.
    /// </para>
    /// </summary>
    public IReadOnlyList<StatusStamp> Stamps
    {
        get
        {
            var stored = string.IsNullOrWhiteSpace(StampsJson)
                ? []
                : JsonSerializer.Deserialize<List<StatusStamp>>(StampsJson) ?? [];

            var byStatus = stored.ToDictionary(s => s.Status);

            return Defaults()
                .Select(d => byStatus.TryGetValue(d.Status, out var configured) ? configured : d)
                .ToList();
        }
    }

    public StatusStamp For(DocumentStatus status) =>
        Stamps.First(s => s.Status == status);

    /// <summary>
    /// Statuses whose stamp must always print and must always warn against use.
    /// <para>
    /// Not configurable away, deliberately. An administrator who blanked the Obsolete stamp
    /// would produce a withdrawn document that reads exactly like a current one — the single
    /// failure that controlled-document systems exist to prevent. Effective and Approved carry
    /// no such floor and may be freely edited or switched off.
    /// </para>
    /// </summary>
    public static IReadOnlySet<DocumentStatus> RequiresWarning { get; } =
        new HashSet<DocumentStatus>
        {
            DocumentStatus.Draft,
            DocumentStatus.InDraftReview,
            DocumentStatus.InReview,
            DocumentStatus.Superseded,
            DocumentStatus.Obsolete,
            DocumentStatus.Withdrawn,
        };

    public void Update(IReadOnlyList<StatusStamp> stamps, string updatedBy)
    {
        ArgumentNullException.ThrowIfNull(stamps);

        foreach (var status in RequiresWarning)
        {
            var stamp = stamps.FirstOrDefault(s => s.Status == status);

            if (stamp is null || !stamp.IsEnabled)
            {
                throw new ArgumentException(
                    $"The {status} stamp cannot be disabled — a document in that state must "
                    + "never print as though it were current.",
                    nameof(stamps));
            }

            if (!ContainsWarning(stamp.Text))
            {
                throw new ArgumentException(
                    $"The {status} stamp must warn against use — include wording such as "
                    + "\"DO NOT USE\" or \"NOT FOR USE\".",
                    nameof(stamps));
            }
        }

        StampsJson = JsonSerializer.Serialize(stamps);
        UpdatedBy = string.IsNullOrWhiteSpace(updatedBy)
            ? throw new ArgumentException("The acting user is required.", nameof(updatedBy))
            : updatedBy;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Checked on the raw template rather than the rendered text: the warning must be constant
    /// wording, not something a token might or might not expand into.
    /// </summary>
    private static bool ContainsWarning(string text) =>
        text.Contains("DO NOT USE", StringComparison.OrdinalIgnoreCase)
        || text.Contains("NOT FOR USE", StringComparison.OrdinalIgnoreCase)
        || text.Contains("NOT A CONTROLLED", StringComparison.OrdinalIgnoreCase);

    public static DocumentStatusStamps CreateDefault(string createdBy) => new(createdBy);

    /// <summary>
    /// The seeded wording. Every stamp answers "is this safe to work from?" before anything
    /// else, because that is the only question a reader urgently needs answered.
    /// <para>
    /// Superseded and Obsolete are worded differently on purpose: one says a later revision
    /// exists, the other says nothing replaces it, and that changes what the reader does next.
    /// Approved is the one most easily got wrong — it is signed but not yet in force, so a bare
    /// "APPROVED" would invite someone to work from it early.
    /// </para>
    /// <para>
    /// Colours are the lifecycle tokens used by the sign-in rail, status chips and dashboard
    /// pipeline, so a printed page and the screen it came from agree on what a colour means.
    /// </para>
    /// </summary>
    public static IReadOnlyList<StatusStamp> Defaults() =>
    [
        new(DocumentStatus.Draft,
            "DRAFT — UNAPPROVED — NOT FOR USE", "7C6FE0", true),

        new(DocumentStatus.InDraftReview,
            "DRAFT UNDER REVIEW — UNAPPROVED — NOT FOR USE", "7C6FE0", true),

        new(DocumentStatus.InReview,
            "IN REVIEW — UNAPPROVED DRAFT — NOT FOR USE", "F0A83C", true),

        new(DocumentStatus.Approved,
            "APPROVED — NOT YET EFFECTIVE — effective {EffectiveDate} — DO NOT USE UNTIL THEN",
            "12A594", true),

        new(DocumentStatus.Effective,
            "EFFECTIVE — {DocumentNumber} Rev {Revision} — effective {EffectiveDate}",
            "1FA971", true),

        new(DocumentStatus.Superseded,
            "SUPERSEDED — REPLACED BY A LATER REVISION — DO NOT USE", "5B7A9D", true),

        new(DocumentStatus.Obsolete,
            "OBSOLETE — WITHDRAWN FROM USE — DO NOT USE", "D65F4C", true),

        new(DocumentStatus.Withdrawn,
            "WITHDRAWN — NEVER ISSUED — NOT A CONTROLLED DOCUMENT", "D65F4C", true),
    ];
}
