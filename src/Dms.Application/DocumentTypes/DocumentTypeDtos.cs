using Dms.Domain.Entities;

namespace Dms.Application.DocumentTypes;

public sealed record DocumentTypeSummary(
    Guid Id,
    string Code,
    string Name,
    bool IsActive,
    DateTimeOffset CreatedAt)
{
    public static DocumentTypeSummary From(DocumentType type) =>
        new(type.Id, type.Code, type.Name, type.IsActive, type.CreatedAt);
}

public sealed record CreateDocumentTypeRequest(string Code, string Name);
