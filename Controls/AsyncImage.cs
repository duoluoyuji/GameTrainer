using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace GameTrainer.Controls;

public class AsyncImage : Image
{
    private static readonly HttpClient Http = CreateHttp();
    private static readonly ConcurrentDictionary<string, BitmapImage> MemoryCache = new();
    private static readonly SemaphoreSlim Throttle = new(6);

    private static string CacheDir => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache", "covers");

    private static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(8),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
        return client;
    }

    public static readonly DependencyProperty SourceUrlProperty = DependencyProperty.Register(
        nameof(SourceUrl), typeof(string), typeof(AsyncImage), new PropertyMetadata("", OnSourceUrlChanged));

    public string SourceUrl
    {
        get => (string)GetValue(SourceUrlProperty);
        set => SetValue(SourceUrlProperty, value);
    }

    private static void OnSourceUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AsyncImage image)
        {
            var url = e.NewValue as string ?? "";
            image.LoadImageAsync(url);
        }
    }

    private async void LoadImageAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            Source = null;
            return;
        }

        if (MemoryCache.TryGetValue(url, out var cached))
        {
            Source = cached;
            return;
        }

        // 检查本地磁盘缓存
        var localCachedPath = GetLocalDiskCachePath(url);
        if (File.Exists(localCachedPath))
        {
            try
            {
                var bmp = LoadBitmapFromFile(localCachedPath);
                if (bmp != null)
                {
                    MemoryCache[url] = bmp;
                    Source = bmp;
                    return;
                }
            }
            catch { }
        }

        await Throttle.WaitAsync();
        try
        {
            var bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
            if (bytes != null && bytes.Length > 0)
            {
                // 写入磁盘缓存
                try
                {
                    Directory.CreateDirectory(CacheDir);
                    await File.WriteAllBytesAsync(localCachedPath, bytes);
                }
                catch { }

                var bmp = CreateBitmapFromBytes(bytes);
                if (bmp != null)
                {
                    MemoryCache[url] = bmp;
                    Dispatcher.Invoke(() => Source = bmp);
                }
            }
        }
        catch
        {
            // 失败保持默认
        }
        finally
        {
            Throttle.Release();
        }
    }

    private static string GetLocalDiskCachePath(string url)
    {
        var hash = Math.Abs(url.GetHashCode()).ToString("X");
        return Path.Combine(CacheDir, $"{hash}.jpg");
    }

    private static BitmapImage? LoadBitmapFromFile(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 320;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private static BitmapImage? CreateBitmapFromBytes(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 320;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }
}
