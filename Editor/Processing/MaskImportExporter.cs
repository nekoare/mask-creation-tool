using UnityEngine;
using UnityEditor;
using System.IO;

namespace NekoareMaskTool.Editor
{
    /// <summary>
    /// マスクインポート時の合成モード
    /// </summary>
    public enum MaskBlendMode
    {
        /// <summary>
        /// 既存の塗りを保護しつつ新しいマスクを追加
        /// </summary>
        Default = 0,
        /// <summary>
        /// 最小値を採用（積集合的）
        /// </summary>
        Min = 1,
        /// <summary>
        /// 最大値を採用（和集合的）
        /// </summary>
        Max = 2,
        /// <summary>
        /// 乗算
        /// </summary>
        Multiply = 3
    }

    /// <summary>
    /// マスクのImport/Export処理
    /// </summary>
    public static class MaskImportExporter
    {
        /// <summary>
        /// マスクをPNGファイルとしてエクスポート
        /// </summary>
        public static void ExportMask(MaskCanvasModel model, string path)
        {
            if (model == null || string.IsNullOrEmpty(path))
            {
                // Debug.LogError("ExportMask: Invalid parameters");
                return;
            }

            var texture = new Texture2D(model.Width, model.Height, TextureFormat.R8, false);
            var pixels = new Color[model.Width * model.Height];

            for (int y = 0; y < model.Height; y++)
            {
                for (int x = 0; x < model.Width; x++)
                {
                    float value = model.GetValue(x, y);
                    int index = y * model.Width + x;
                    pixels[index] = new Color(value, value, value, 1f);
                }
            }

            texture.SetPixels(pixels);
            texture.Apply();

            byte[] bytes = texture.EncodeToPNG();
            File.WriteAllBytes(path, bytes);

            Object.DestroyImmediate(texture);

            // Unityにリフレッシュさせる
            AssetDatabase.Refresh();

            // Debug.Log($"Mask exported to: {path}");
        }

        /// <summary>
        /// PNGファイルからマスクをインポート
        /// </summary>
        public static bool ImportMask(MaskCanvasModel model, string path, MaskBlendMode blendMode = MaskBlendMode.Default)
        {
            if (model == null || string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                // Debug.LogError("ImportMask: Invalid parameters or file not found");
                return false;
            }

            byte[] bytes = File.ReadAllBytes(path);
            var texture = new Texture2D(2, 2);

            if (!texture.LoadImage(bytes))
            {
                // Debug.LogError("ImportMask: Failed to load image");
                Object.DestroyImmediate(texture);
                return false;
            }

            // サイズチェック
            if (texture.width != model.Width || texture.height != model.Height)
            {
                // Debug.LogWarning($"ImportMask: Size mismatch. Expected {model.Width}x{model.Height}, got {texture.width}x{texture.height}. Resizing...");

                // リサイズ
                var resizedTexture = ResizeTexture(texture, model.Width, model.Height);
                Object.DestroyImmediate(texture);
                texture = resizedTexture;
            }

            var pixels = texture.GetPixels();

            for (int y = 0; y < model.Height; y++)
            {
                for (int x = 0; x < model.Width; x++)
                {
                    int index = y * model.Width + x;
                    float importValue = pixels[index].grayscale;
                    float existingValue = model.GetValue(x, y);
                    float resultValue = BlendValues(existingValue, importValue, blendMode);
                    model.SetValue(x, y, resultValue);
                }
            }

            model.SyncCpuToGpu();

            Object.DestroyImmediate(texture);

            // Debug.Log($"Mask imported from: {path}");
            return true;
        }

        /// <summary>
        /// 合成モードに基づいて値をブレンド
        /// </summary>
        private static float BlendValues(float existing, float imported, MaskBlendMode mode)
        {
            switch (mode)
            {
                case MaskBlendMode.Default:
                    // 既存が塗られている（< 1）かつインポートが塗られていない（≈ 1）→ 既存を維持
                    // それ以外 → インポートの値を適用
                    if (existing < 1f && imported >= 0.999f)
                    {
                        return existing;
                    }
                    return imported;

                case MaskBlendMode.Min:
                    // 濃さが薄い方を採用（内部マスク値は大きい方）
                    return Mathf.Max(existing, imported);

                case MaskBlendMode.Max:
                    // 濃さが濃い方を採用（内部マスク値は小さい方）
                    return Mathf.Min(existing, imported);

                case MaskBlendMode.Multiply:
                    return existing * imported;

                default:
                    return imported;
            }
        }

        /// <summary>
        /// テクスチャをリサイズ
        /// </summary>
        private static Texture2D ResizeTexture(Texture2D source, int targetWidth, int targetHeight)
        {
            var renderTexture = RenderTexture.GetTemporary(targetWidth, targetHeight);
            Graphics.Blit(source, renderTexture);

            RenderTexture.active = renderTexture;
            var result = new Texture2D(targetWidth, targetHeight, TextureFormat.RGBA32, false);
            result.ReadPixels(new Rect(0, 0, targetWidth, targetHeight), 0, 0);
            result.Apply();

            RenderTexture.active = null;
            RenderTexture.ReleaseTemporary(renderTexture);

            return result;
        }

        /// <summary>
        /// Export用のファイルダイアログを表示
        /// </summary>
        public static string ShowExportDialog(string defaultName = "mask")
        {
            return EditorUtility.SaveFilePanel(
                "Export Mask",
                "Assets",
                defaultName,
                "png"
            );
        }

        /// <summary>
        /// Import用のファイルダイアログを表示
        /// </summary>
        public static string ShowImportDialog()
        {
            return EditorUtility.OpenFilePanel(
                "Import Mask",
                "Assets",
                "png"
            );
        }
    }
}
