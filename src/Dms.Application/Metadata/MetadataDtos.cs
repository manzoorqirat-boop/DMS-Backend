using Dms.Domain.Entities;
using Dms.Domain.Enums;

namespace Dms.Application.Metadata;

public sealed record CreateMetadataFieldRequest(
    Guid DocumentTypeId,
    string Tag,
    string Label,
    MetadataSource Source,
    int DisplayOrder,
    bool IsRequired);

public sealed record UpdateMetadataFieldRequest(
    string Label,
    MetadataSource Source,
    int DisplayOrder,
    bool IsRequired);

public sealed record MetadataFieldView(
    Guid Id,
    Guid DocumentTypeId,
    string Tag,
    string Label,
    MetadataSource Source,
    int DisplayOrder,
    bool IsRequired,
    DateTimeOffset CreatedAt)
{
    public static MetadataFieldView From(MetadataFieldDefinition field) => new(
        field.Id,
        field.DocumentTypeId,
        field.Tag,
        field.Label,
        field.Source,
        field.DisplayOrder,
        field.IsRequired,
        field.CreatedAt);
}
