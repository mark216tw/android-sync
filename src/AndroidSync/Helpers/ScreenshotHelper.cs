using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Com.AndroidSync.App.Helpers;

public static class ScreenshotHelper
{
    public static async Task<(bool Success, string Message)> SaveAndCopyBgraAsync(
        byte[] bgraData,
        int width,
        int height,
        bool copyToClipboard = true)
    {
        if (bgraData.Length < width * height * 4 || width <= 0 || height <= 0)
        {
            return (false, "目前沒有可擷取的畫面影格。");
        }

        var picturesDir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        var outputDir = Path.Combine(
            string.IsNullOrEmpty(picturesDir) ? AppContext.BaseDirectory : picturesDir,
            "AndroidSync");
        Directory.CreateDirectory(outputDir);

        var fileName = $"AndroidSync_{DateTime.Now:yyyyMMdd_HHmmss}.png";
        var filePath = Path.Combine(outputDir, fileName);

        using var memoryStream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, memoryStream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            (uint)width,
            (uint)height,
            96.0,
            96.0,
            bgraData);
        await encoder.FlushAsync();

        memoryStream.Seek(0);
        await using (var fileStream = File.Create(filePath))
        {
            await memoryStream.AsStreamForRead().CopyToAsync(fileStream);
        }

        if (copyToClipboard)
        {
            try
            {
                memoryStream.Seek(0);
                var package = new DataPackage();
                package.SetBitmap(RandomAccessStreamReference.CreateFromStream(memoryStream));
                Clipboard.SetContent(package);
                Clipboard.Flush();
            }
            catch
            {
                return (true, $"截圖已儲存至圖片\\AndroidSync\\{fileName}（剪貼簿暫時無法寫入）");
            }
        }

        return (true, $"截圖已儲存並複製到剪貼簿：{fileName}");
    }
}
