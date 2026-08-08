namespace Amane.Mailer.Attachments.Validation;

/// <summary>Dispatches structural content validation to the type-specific validator (ADR 0022 D-06).</summary>
public static class AttachmentContentValidator
{
    public static AttachmentStructureResult Validate(AttachmentFileType type, byte[] content) =>
        type switch
        {
            AttachmentFileType.Pdf => PdfStructureValidator.Validate(content),
            AttachmentFileType.Jpeg => JpegStructureValidator.Validate(content),
            AttachmentFileType.Png => PngStructureValidator.Validate(content),
            AttachmentFileType.Docx => OfficeOpenXmlStructureValidator.Validate(content, AttachmentFileType.Docx),
            AttachmentFileType.Xlsx => OfficeOpenXmlStructureValidator.Validate(content, AttachmentFileType.Xlsx),
            AttachmentFileType.Csv => TextContentValidator.ValidateCsv(content),
            AttachmentFileType.Txt => TextContentValidator.ValidateTxt(content),
            _ => AttachmentStructureResult.TypeNotAllowed(),
        };
}
