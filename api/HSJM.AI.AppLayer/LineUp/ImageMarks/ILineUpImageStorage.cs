using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer.LineUp.ImageMarks;

public interface ILineUpImageStorage
{
    Task<LineUpStoredImage> StoreAsync(
        Stream source,
        LineUpImageDescriptor descriptor,
        CancellationToken cancellationToken);

    Task<Stream> OpenReadAsync(
        string storageKey,
        CancellationToken cancellationToken);
}

public sealed record LineUpImageDescriptor(long ImageId, string OriginalFileName);

public sealed record LineUpStoredImage(
    string StorageKey,
    string ContentSha256,
    long ByteLength,
    int PixelWidth,
    int PixelHeight,
    string DetectedMimeType);
