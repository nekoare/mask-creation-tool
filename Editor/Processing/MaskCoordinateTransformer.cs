using System.Collections.Generic;
using UnityEngine;

namespace NekoareMaskTool.Editor
{
    /// <summary>
    /// 複数の元マスクをアトラス座標系に変換して1枚のマスクに合成する
    /// TextureAtlasBuilder.BlitIslandToAtlas と同じ座標変換ロジックを使用
    /// </summary>
    public static class MaskCoordinateTransformer
    {
        /// <summary>
        /// 複数の元マスクをアトラス座標系に変換して1枚のマスクに合成
        /// </summary>
        /// <param name="sourceMasks">各アイランドの元マスク情報</param>
        /// <param name="resolution">出力マスクの解像度</param>
        /// <returns>アトラス座標系のマスク値配列（resolution x resolution）</returns>
        public static float[] TransformToAtlas(
            List<SourceMaskEntry> sourceMasks,
            int resolution)
        {
            // 白（1.0 = マスクなし）で初期化
            var result = new float[resolution * resolution];
            for (int i = 0; i < result.Length; i++)
                result[i] = 1.0f;

            if (sourceMasks == null)
                return result;

            foreach (var entry in sourceMasks)
            {
                BlitMaskToAtlas(result, entry, resolution);
            }

            return result;
        }

        /// <summary>
        /// 複数の元テクスチャをアトラス座標系に変換して1枚の背景テクスチャに合成
        /// </summary>
        /// <param name="sources">各アイランドの元テクスチャ情報（mainTextureを使用）</param>
        /// <param name="resolution">出力テクスチャの解像度</param>
        /// <returns>アトラス座標系の合成テクスチャ</returns>
        public static Texture2D ComposeAtlasBackground(
            List<SourceMaskEntry> sources,
            int resolution)
        {
            var pixels = new Color[resolution * resolution];
            // 透明で初期化（テクスチャがない領域は透明）
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = Color.clear;

            if (sources != null)
            {
                foreach (var entry in sources)
                {
                    BlitColorToAtlas(pixels, entry, resolution);
                }
            }

            var texture = new Texture2D(resolution, resolution, TextureFormat.RGBA32, false);
            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
        }

        /// <summary>
        /// 1アイランド分のカラーテクスチャをアトラスに配置
        /// </summary>
        private static void BlitColorToAtlas(
            Color[] atlas,
            SourceMaskEntry entry,
            int resolution)
        {
            if (entry.mainTexture == null)
                return;

            int startX = Mathf.FloorToInt(entry.atlasPosition.x * resolution);
            int startY = Mathf.FloorToInt(entry.atlasPosition.y * resolution);
            int width = Mathf.Max(Mathf.FloorToInt(entry.atlasScale.x * resolution), 1);
            int height = Mathf.Max(Mathf.FloorToInt(entry.atlasScale.y * resolution), 1);

            Texture2D readable = GetReadableTexture(entry.mainTexture);

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int atlasX = startX + x;
                    int atlasY = startY + y;

                    if (atlasX < 0 || atlasX >= resolution ||
                        atlasY < 0 || atlasY >= resolution)
                        continue;

                    float normalizedX = (float)x / width;
                    float normalizedY = (float)y / height;

                    float u = entry.originalBounds.x + normalizedX * entry.originalBounds.width;
                    float v = entry.originalBounds.y + normalizedY * entry.originalBounds.height;

                    Color pixel = SampleTextureBilinear(readable, u, v);
                    atlas[atlasY * resolution + atlasX] = pixel;
                }
            }

            if (readable != entry.mainTexture)
                Object.DestroyImmediate(readable);
        }

        /// <summary>
        /// 1アイランド分のマスクをアトラスに配置（ブレンドモード付き）
        /// </summary>
        public static void BlitMaskToAtlas(
            float[] atlas,
            SourceMaskEntry entry,
            int resolution,
            MaskBlendMode blendMode)
        {
            int startX = Mathf.FloorToInt(entry.atlasPosition.x * resolution);
            int startY = Mathf.FloorToInt(entry.atlasPosition.y * resolution);
            int width = Mathf.Max(Mathf.FloorToInt(entry.atlasScale.x * resolution), 1);
            int height = Mathf.Max(Mathf.FloorToInt(entry.atlasScale.y * resolution), 1);

            if (entry.maskTexture == null)
                return;

            Texture2D readable = GetReadableTexture(entry.maskTexture);

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int atlasX = startX + x;
                    int atlasY = startY + y;

                    if (atlasX < 0 || atlasX >= resolution ||
                        atlasY < 0 || atlasY >= resolution)
                        continue;

                    float normalizedX = (float)x / width;
                    float normalizedY = (float)y / height;

                    float u = entry.originalBounds.x + normalizedX * entry.originalBounds.width;
                    float v = entry.originalBounds.y + normalizedY * entry.originalBounds.height;

                    Color pixel = SampleTextureBilinear(readable, u, v);
                    float imported = pixel.grayscale;
                    float existing = atlas[atlasY * resolution + atlasX];
                    atlas[atlasY * resolution + atlasX] = BlendValues(existing, imported, blendMode);
                }
            }

            if (readable != entry.maskTexture)
                Object.DestroyImmediate(readable);
        }

        /// <summary>
        /// 1アイランド分のマスクをアトラスに配置（上書き、内部用）
        /// </summary>
        private static void BlitMaskToAtlas(
            float[] atlas,
            SourceMaskEntry entry,
            int resolution)
        {
            // アトラス上の配置範囲（ピクセル）
            int startX = Mathf.FloorToInt(entry.atlasPosition.x * resolution);
            int startY = Mathf.FloorToInt(entry.atlasPosition.y * resolution);
            int width = Mathf.Max(Mathf.FloorToInt(entry.atlasScale.x * resolution), 1);
            int height = Mathf.Max(Mathf.FloorToInt(entry.atlasScale.y * resolution), 1);

            // マスクテクスチャがない場合は白のまま（初期値）
            if (entry.maskTexture == null)
                return;

            Texture2D readable = GetReadableTexture(entry.maskTexture);

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int atlasX = startX + x;
                    int atlasY = startY + y;

                    if (atlasX < 0 || atlasX >= resolution ||
                        atlasY < 0 || atlasY >= resolution)
                        continue;

                    // 配置領域内での正規化座標 (0-1)
                    float normalizedX = (float)x / width;
                    float normalizedY = (float)y / height;

                    // 元のUV範囲にマッピング
                    float u = entry.originalBounds.x + normalizedX * entry.originalBounds.width;
                    float v = entry.originalBounds.y + normalizedY * entry.originalBounds.height;

                    // グレースケール値をサンプリング
                    Color pixel = SampleTextureBilinear(readable, u, v);
                    atlas[atlasY * resolution + atlasX] = pixel.grayscale;
                }
            }

            if (readable != entry.maskTexture)
                Object.DestroyImmediate(readable);
        }

        /// <summary>
        /// テクスチャをバイリニア補間でサンプリング
        /// TextureAtlasBuilder.SampleTexture と同じロジック
        /// </summary>
        private static Color SampleTextureBilinear(Texture2D texture, float u, float v)
        {
            // UV範囲外は白（マスクなし）を返す
            if (u < 0 || u > 1 || v < 0 || v > 1)
                return Color.white;

            float x = u * (texture.width - 1);
            float y = v * (texture.height - 1);

            int x0 = Mathf.FloorToInt(x);
            int y0 = Mathf.FloorToInt(y);
            int x1 = Mathf.Min(x0 + 1, texture.width - 1);
            int y1 = Mathf.Min(y0 + 1, texture.height - 1);

            float fx = x - x0;
            float fy = y - y0;

            Color c00 = texture.GetPixel(x0, y0);
            Color c10 = texture.GetPixel(x1, y0);
            Color c01 = texture.GetPixel(x0, y1);
            Color c11 = texture.GetPixel(x1, y1);

            Color c0 = Color.Lerp(c00, c10, fx);
            Color c1 = Color.Lerp(c01, c11, fx);

            return Color.Lerp(c0, c1, fy);
        }

        /// <summary>
        /// 合成モードに基づいて値をブレンド
        /// </summary>
        private static float BlendValues(float existing, float imported, MaskBlendMode mode)
        {
            switch (mode)
            {
                case MaskBlendMode.Default:
                    if (existing < 1f && imported >= 0.999f)
                        return existing;
                    return imported;
                case MaskBlendMode.Min:
                    return Mathf.Max(existing, imported);
                case MaskBlendMode.Max:
                    return Mathf.Min(existing, imported);
                case MaskBlendMode.Multiply:
                    return existing * imported;
                default:
                    return imported;
            }
        }

        /// <summary>
        /// 読み取り可能なテクスチャを取得
        /// TextureAtlasBuilder.GetReadableTexture と同じロジック
        /// </summary>
        private static Texture2D GetReadableTexture(Texture2D source)
        {
            if (source.isReadable)
                return source;

            RenderTexture tmp = RenderTexture.GetTemporary(
                source.width,
                source.height,
                0,
                RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.sRGB
            );

            Graphics.Blit(source, tmp);

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = tmp;

            Texture2D readable = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
            readable.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
            readable.Apply();

            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(tmp);

            return readable;
        }
    }
}
