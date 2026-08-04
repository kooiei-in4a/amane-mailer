using System.IO.Compression;
using System.Text;
using Amane.Mailer.Attachments.Validation;
using Amane.Mailer.Contracts.MailRequests;

namespace Amane.Mailer.Tests.Attachments.Validation;

public sealed class AttachmentFilenameValidatorTests
{
    [Theory]
    [InlineData("invoice.pdf")]
    [InlineData("請求書.pdf")]
    [InlineData("a")]
    public void TryValidate_accepts_reasonable_filenames(string fileName)
    {
        Assert.True(AttachmentFilenameValidator.TryValidate(fileName, out var normalized));
        Assert.Equal(fileName.Normalize(System.Text.NormalizationForm.FormC), normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b.pdf")]
    [InlineData("a\\b.pdf")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("con")]
    [InlineData("CON.txt")]
    [InlineData("lpt1.pdf")]
    public void TryValidate_rejects_invalid_filenames(string fileName)
    {
        Assert.False(AttachmentFilenameValidator.TryValidate(fileName, out _));
    }

    [Fact]
    public void TryValidate_rejects_control_characters_and_nul()
    {
        Assert.False(AttachmentFilenameValidator.TryValidate("a\0b.pdf", out _));
        Assert.False(AttachmentFilenameValidator.TryValidate("a\nb.pdf", out _));
    }

    [Fact]
    public void TryValidate_rejects_names_over_255_utf8_bytes()
    {
        var longName = new string('a', 256);
        Assert.False(AttachmentFilenameValidator.TryValidate(longName, out _));
    }

    [Fact]
    public void HasCaseInsensitiveDuplicate_detects_case_variants()
    {
        Assert.True(AttachmentFilenameValidator.HasCaseInsensitiveDuplicate(["Invoice.pdf", "invoice.PDF"]));
        Assert.False(AttachmentFilenameValidator.HasCaseInsensitiveDuplicate(["invoice.pdf", "photo.jpg"]));
    }
}

public sealed class AttachmentFileTypeCatalogTests
{
    [Theory]
    [InlineData("pdf", AttachmentFileType.Pdf, "application/pdf")]
    [InlineData("jpg", AttachmentFileType.Jpeg, "image/jpeg")]
    [InlineData("jpeg", AttachmentFileType.Jpeg, "image/jpeg")]
    [InlineData("png", AttachmentFileType.Png, "image/png")]
    [InlineData("csv", AttachmentFileType.Csv, "text/csv")]
    [InlineData("txt", AttachmentFileType.Txt, "text/plain")]
    public void TryGetByExtension_maps_known_extensions(string extension, AttachmentFileType expectedType, string expectedContentType)
    {
        Assert.True(AttachmentFileTypeCatalog.TryGetByExtension(extension, out var type, out var contentType));
        Assert.Equal(expectedType, type);
        Assert.Equal(expectedContentType, contentType);
    }

    [Fact]
    public void TryGetByExtension_rejects_unknown_extension()
    {
        Assert.False(AttachmentFileTypeCatalog.TryGetByExtension("exe", out _, out _));
    }

    [Fact]
    public void ExtractExtension_uses_last_segment()
    {
        Assert.Equal("gz", AttachmentFileTypeCatalog.ExtractExtension("archive.tar.gz"));
        Assert.Equal(string.Empty, AttachmentFileTypeCatalog.ExtractExtension("noext"));
    }
}

public sealed class PdfStructureValidatorTests
{
    private static byte[] BuildMinimalPdf(bool encrypted = false)
    {
        var body = new StringBuilder();
        body.Append("%PDF-1.4\n");
        body.Append("1 0 obj\n<< /Type /Catalog >>\nendobj\n");
        if (encrypted)
        {
            body.Append("2 0 obj\n<< /Filter /Standard /Encrypt true >>\nendobj\n");
        }

        body.Append("trailer\n<< /Root 1 0 R");
        if (encrypted)
        {
            body.Append(" /Encrypt 2 0 R");
        }

        body.Append(" >>\nstartxref\n0\n%%EOF\n");
        return Encoding.ASCII.GetBytes(body.ToString());
    }

    [Fact]
    public void Validate_accepts_minimal_well_formed_pdf()
    {
        var result = PdfStructureValidator.Validate(BuildMinimalPdf());
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_rejects_missing_signature()
    {
        var bytes = Encoding.ASCII.GetBytes("not a pdf");
        var result = PdfStructureValidator.Validate(bytes);
        Assert.False(result.IsValid);
        Assert.Equal(MailerErrorCodes.AttachmentContentMismatch, result.FailureCode);
    }

    [Fact]
    public void Validate_rejects_trailing_payload_after_eof()
    {
        var bytes = BuildMinimalPdf();
        var withTrailer = new byte[bytes.Length + 5];
        bytes.CopyTo(withTrailer, 0);
        Encoding.ASCII.GetBytes("HELLO").CopyTo(withTrailer, bytes.Length);

        var result = PdfStructureValidator.Validate(withTrailer);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_rejects_encrypted_pdf()
    {
        var result = PdfStructureValidator.Validate(BuildMinimalPdf(encrypted: true));
        Assert.False(result.IsValid);
        Assert.Equal(MailerErrorCodes.AttachmentEncrypted, result.FailureCode);
    }
}

public sealed class JpegStructureValidatorTests
{
    [Fact]
    public void Validate_accepts_minimal_marker_sequence()
    {
        byte[] bytes = [0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x04, 0x00, 0x00, 0xFF, 0xD9];
        var result = JpegStructureValidator.Validate(bytes);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_rejects_missing_soi()
    {
        byte[] bytes = [0x00, 0x00, 0xFF, 0xD9];
        Assert.False(JpegStructureValidator.Validate(bytes).IsValid);
    }

    [Fact]
    public void Validate_rejects_trailing_payload_after_eoi()
    {
        byte[] bytes = [0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x04, 0x00, 0x00, 0xFF, 0xD9, 0x00];
        Assert.False(JpegStructureValidator.Validate(bytes).IsValid);
    }
}

public sealed class PngStructureValidatorTests
{
    // Minimal valid 1x1 RGBA PNG (verified independently against Python's zlib.crc32).
    private static readonly byte[] MinimalPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==");

    [Fact]
    public void Validate_accepts_minimal_png()
    {
        var result = PngStructureValidator.Validate(MinimalPng);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_rejects_corrupted_crc()
    {
        var corrupted = (byte[])MinimalPng.Clone();
        corrupted[^5] ^= 0xFF; // flip a byte inside the IEND CRC
        Assert.False(PngStructureValidator.Validate(corrupted).IsValid);
    }

    [Fact]
    public void Validate_rejects_trailing_payload()
    {
        var withTrailer = new byte[MinimalPng.Length + 4];
        MinimalPng.CopyTo(withTrailer, 0);
        Assert.False(PngStructureValidator.Validate(withTrailer).IsValid);
    }
}

public sealed class OfficeOpenXmlStructureValidatorTests
{
    private static byte[] BuildDocx(bool includeMacro = false, bool includeRequiredPart = true)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "[Content_Types].xml", "<Types/>");
            if (includeRequiredPart)
            {
                WriteEntry(archive, "word/document.xml", "<document/>");
            }

            if (includeMacro)
            {
                WriteEntry(archive, "word/vbaProject.bin", "macro");
            }
        }

        return stream.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    [Fact]
    public void Validate_accepts_minimal_docx()
    {
        var result = OfficeOpenXmlStructureValidator.Validate(BuildDocx(), AttachmentFileType.Docx);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_rejects_macro_enabled_docx()
    {
        var result = OfficeOpenXmlStructureValidator.Validate(BuildDocx(includeMacro: true), AttachmentFileType.Docx);
        Assert.False(result.IsValid);
        Assert.Equal(MailerErrorCodes.AttachmentTypeNotAllowed, result.FailureCode);
    }

    [Fact]
    public void Validate_rejects_docx_missing_required_part()
    {
        var result = OfficeOpenXmlStructureValidator.Validate(
            BuildDocx(includeRequiredPart: false),
            AttachmentFileType.Docx);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_rejects_non_zip_content()
    {
        var result = OfficeOpenXmlStructureValidator.Validate([0x00, 0x01, 0x02], AttachmentFileType.Docx);
        Assert.False(result.IsValid);
    }
}

public sealed class TextContentValidatorTests
{
    [Fact]
    public void ValidateTxt_accepts_plain_utf8_text()
    {
        var bytes = Encoding.UTF8.GetBytes("hello\nworld\n日本語\n");
        Assert.True(TextContentValidator.ValidateTxt(bytes).IsValid);
    }

    [Fact]
    public void ValidateTxt_accepts_and_allows_bom()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("hello")).ToArray();
        Assert.True(TextContentValidator.ValidateTxt(bytes).IsValid);
    }

    [Fact]
    public void ValidateTxt_rejects_nul_byte()
    {
        var bytes = Encoding.UTF8.GetBytes("hello\0world");
        Assert.False(TextContentValidator.ValidateTxt(bytes).IsValid);
    }

    [Fact]
    public void ValidateTxt_rejects_invalid_utf8()
    {
        byte[] bytes = [0xFF, 0xFE, 0x00];
        Assert.False(TextContentValidator.ValidateTxt(bytes).IsValid);
    }

    [Fact]
    public void ValidateTxt_rejects_overlong_line()
    {
        var bytes = Encoding.UTF8.GetBytes(new string('a', 64 * 1024 + 1));
        Assert.False(TextContentValidator.ValidateTxt(bytes).IsValid);
    }

    [Fact]
    public void ValidateCsv_accepts_quoted_fields_with_escaped_quotes()
    {
        var bytes = Encoding.UTF8.GetBytes("a,\"b,c\",\"d\"\"e\"\n1,2,3\n");
        Assert.True(TextContentValidator.ValidateCsv(bytes).IsValid);
    }

    [Fact]
    public void ValidateCsv_rejects_unterminated_quote()
    {
        var bytes = Encoding.UTF8.GetBytes("a,\"b,c\n1,2\n");
        Assert.False(TextContentValidator.ValidateCsv(bytes).IsValid);
    }
}
