using Microsoft.AspNetCore.Http.Features;
using TeslaBridge.Options;

namespace TeslaBridge.Web;

/// <summary>
/// Optional 3D model of the car for the dashboard. The bridge ships none (car models are usually
/// licensed per user); you bring your own .glb, either as a file in ./config/bridge/car.glb or by
/// uploading it on the dashboard's settings page.
/// </summary>
public static class CarModelApi
{
    public const long MaxBytes = 80L * 1024 * 1024;

    public static string UploadedPath(BridgeOptions o) =>
        Path.Combine(Path.GetDirectoryName(o.StateFile) ?? "/data", "car-model.glb");

    /// <summary>The uploaded model wins over the file in config.</summary>
    public static (string Path, string Source)? Find(BridgeOptions o)
    {
        var up = UploadedPath(o);
        if (File.Exists(up)) return (up, "uploaded");
        if (!string.IsNullOrWhiteSpace(o.Web.CarModelFile) && File.Exists(o.Web.CarModelFile)) return (o.Web.CarModelFile, "file");
        return null;
    }

    /// <summary>glTF binary starts with "glTF" and version 2.</summary>
    internal static bool LooksLikeGlb(ReadOnlySpan<byte> head) =>
        head.Length >= 8 && head[0] == (byte)'g' && head[1] == (byte)'l' && head[2] == (byte)'T' && head[3] == (byte)'F'
        && BitConverter.ToUInt32(head[4..8]) == 2;

    public static void MapCarModelApi(this RouteGroupBuilder api, BridgeOptions opts)
    {
        api.MapGet("/car-model/info", () =>
        {
            var m = Find(opts);
            return Results.Json(m is null
                ? new { present = false, source = (string?)null, size = 0L, updated_at = (string?)null, file = opts.Web.CarModelFile }
                : new { present = true, source = (string?)m.Value.Source, size = new FileInfo(m.Value.Path).Length,
                        updated_at = (string?)File.GetLastWriteTimeUtc(m.Value.Path).ToString("O"), file = opts.Web.CarModelFile });
        });

        api.MapGet("/car-model", (HttpContext ctx) =>
        {
            var m = Find(opts);
            if (m is null) return Results.NotFound();
            var info = new FileInfo(m.Value.Path);
            ctx.Response.Headers.CacheControl = "private, max-age=0, must-revalidate";
            return Results.File(Path.GetFullPath(m.Value.Path), "model/gltf-binary", lastModified: info.LastWriteTimeUtc, enableRangeProcessing: false,
                entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}\""));
        });

        api.MapPost("/car-model", async (HttpContext ctx, CancellationToken ct) =>
        {
            if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = MaxBytes;
            if (ctx.Request.ContentLength is > MaxBytes) return Results.Json(new { error = $"The file is larger than {MaxBytes / 1024 / 1024} MB" }, statusCode: 413);

            var target = UploadedPath(opts);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var tmp = target + ".upload";
            long size;
            try
            {
                await using (var f = File.Create(tmp)) await ctx.Request.Body.CopyToAsync(f, ct);
                size = new FileInfo(tmp).Length;
                var head = new byte[8];
                await using (var f = File.OpenRead(tmp)) _ = await f.ReadAsync(head, ct);
                if (!LooksLikeGlb(head))
                {
                    File.Delete(tmp);
                    return Results.Json(new { error = "Not a glTF binary (.glb) file. Convert .gltf/.obj/.fbx to .glb first, e.g. with Blender." }, statusCode: 400);
                }
            }
            catch (BadHttpRequestException)
            {
                File.Delete(tmp);
                return Results.Json(new { error = $"The file is larger than {MaxBytes / 1024 / 1024} MB" }, statusCode: 413);
            }
            File.Move(tmp, target, overwrite: true);
            return Results.Json(new { present = true, source = "uploaded", size });
        });

        api.MapDelete("/car-model", () =>
        {
            var up = UploadedPath(opts);
            if (File.Exists(up)) File.Delete(up);
            return Results.Json(new { present = Find(opts) is not null });
        });
    }
}
