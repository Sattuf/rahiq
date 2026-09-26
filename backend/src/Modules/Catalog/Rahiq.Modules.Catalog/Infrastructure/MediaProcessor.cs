using Microsoft.EntityFrameworkCore;
using Rahiq.Application.Abstractions;
using Rahiq.Infrastructure.Common.Persistence;
using Rahiq.Modules.Catalog.Application;
using Rahiq.Modules.Catalog.Domain;
using Rahiq.SharedKernel;
using SkiaSharp;

namespace Rahiq.Modules.Catalog.Infrastructure;

public sealed record UploadMediaCommand(Guid ProductId, string Role, byte[] Content, bool IsGenerated, IReadOnlyDictionary<string, string> Alt, int SortOrder)
    : ICommand<Result<MediaDto>>;

public sealed record DeleteMediaCommand(Guid ProductId, Guid MediaId) : ICommand<Result>;

/// <summary>
/// Product images (security.md §7): the bytes must really be an image (magic numbers, then a full decode), the image
/// is re-encoded to WebP (which drops EXIF, GPS and any embedded payload) and resized to at most 2400 px.
/// </summary>
internal sealed class MediaHandlers(RahiqDbContext db, IBlobStorage storage, IAuditLog audit)
    : IRequestHandler<UploadMediaCommand, Result<MediaDto>>, IRequestHandler<DeleteMediaCommand, Result>
{
    private const int MaxBytes = 15 * 1024 * 1024;
    private const int MaxEdge = 2400;

    public async Task<Result<MediaDto>> Handle(UploadMediaCommand request, CancellationToken cancellationToken)
    {
        var product = await db.Set<Product>().Include(p => p.Media).FirstOrDefaultAsync(p => p.Id == request.ProductId, cancellationToken);
        if (product is null)
        {
            return Error.NotFound("product.not_found", "Product not found.");
        }

        if (request.Content.Length is 0 or > MaxBytes || !LooksLikeImage(request.Content))
        {
            return Error.Validation("media.not_an_image", "Upload a JPEG, PNG or WebP image up to 15 MB.");
        }

        var encoded = Reencode(request.Content);
        if (encoded is null)
        {
            return Error.Validation("media.not_an_image", "The file could not be read as an image.");
        }

        var (bytes, width, height) = encoded.Value;
        var mediaId = Ids.New();
        var key = $"products/{product.Id:N}/{mediaId:N}.webp";
        var created = ProductMedia.Create(product.Id, request.Role, key, "image/webp", width, height, request.IsGenerated,
            new Dictionary<string, string>(request.Alt), request.SortOrder);
        if (created.IsFailure)
        {
            return created.Error;
        }

        using (var stream = new MemoryStream(bytes))
        {
            await storage.PutAsync(key, stream, "image/webp", cancellationToken);
        }

        product.AddMedia(created.Value);
        audit.Record("media.uploaded", "product", product.Id.ToString(), new { request.Role, request.IsGenerated, width, height });
        return new MediaDto(created.Value.Id, storage.GetUrl(key).ToString(), width, height, request.Role, request.Alt.GetValueOrDefault("tr"));
    }

    public async Task<Result> Handle(DeleteMediaCommand request, CancellationToken cancellationToken)
    {
        var media = await db.Set<ProductMedia>().FirstOrDefaultAsync(m => m.Id == request.MediaId && m.ProductId == request.ProductId, cancellationToken);
        if (media is null)
        {
            return Error.NotFound("media.not_found", "Image not found.");
        }

        db.Remove(media);
        await storage.DeleteAsync(media.StorageKey, cancellationToken);
        return Result.Success();
    }

    internal static bool LooksLikeImage(ReadOnlySpan<byte> b) =>
        (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) ||
        (b.Length > 8 && b[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) ||
        (b.Length > 12 && b[..4].SequenceEqual("RIFF"u8) && b.Slice(8, 4).SequenceEqual("WEBP"u8));

    internal static (byte[] Bytes, int Width, int Height)? Reencode(byte[] content)
    {
        using var original = SKBitmap.Decode(content);
        if (original is null || original.Width < 1 || original.Height < 1)
        {
            return null;
        }

        var scale = Math.Min(1.0, (double)MaxEdge / Math.Max(original.Width, original.Height));
        var width = (int)Math.Round(original.Width * scale);
        var height = (int)Math.Round(original.Height * scale);

        using var resized = scale < 1.0 ? original.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKCubicResampler.Mitchell)) : null;
        using var image = SKImage.FromBitmap(resized ?? original);
        using var data = image.Encode(SKEncodedImageFormat.Webp, 86);
        return (data.ToArray(), width, height);
    }
}
