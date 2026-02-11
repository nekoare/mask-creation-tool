using UnityEngine;

namespace NekoareMaskTool.Editor
{
    /// <summary>
    /// マスクデータを管理するモデルクラス
    /// </summary>
    public class MaskCanvasModel
    {
        private int _width;
        private int _height;
        private float[] _maskValues;
        private RenderTexture _maskTexture;
        private Texture2D _backgroundTexture;
        private RenderTexture _previewTexture;     // Scene表示用（MeshDeleterWithTexture方式）

        public int Width => _width;
        public int Height => _height;
        public float[] MaskValues => _maskValues;
        public RenderTexture MaskTexture => _maskTexture;
        public Texture2D BackgroundTexture => _backgroundTexture;
        public RenderTexture PreviewTexture => _previewTexture;

        public void Initialize(int width, int height)
        {
            _width = width;
            _height = height;

            // CPU側バッファを初期化（白 = 1.0で埋める）
            _maskValues = new float[width * height];
            for (int i = 0; i < _maskValues.Length; i++)
            {
                _maskValues[i] = 1.0f;
            }

            // GPU側テクスチャを初期化
            if (_maskTexture != null)
            {
                _maskTexture.Release();
            }

            _maskTexture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
            _maskTexture.enableRandomWrite = true;
            _maskTexture.filterMode = FilterMode.Bilinear;  // エッジのギザギザを軽減
            _maskTexture.Create();

            // プレビュー用テクスチャを初期化（MeshDeleterWithTexture方式）
            if (_previewTexture != null)
            {
                _previewTexture.Release();
            }
            _previewTexture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
            _previewTexture.enableRandomWrite = true;
            _previewTexture.Create();

            // CPU -> GPU同期
            SyncCpuToGpu();
        }

        public void SetBackgroundTexture(Texture2D texture)
        {
            _backgroundTexture = texture;
        }

        /// <summary>
        /// PreviewTextureを背景テクスチャで初期化（MeshDeleterWithTexture方式）
        /// 背景テクスチャの内容をそのままPreviewTextureにコピーする
        /// </summary>
        public void InitializePreviewFromBackground()
        {
            if (_previewTexture == null || _backgroundTexture == null)
                return;

            Graphics.Blit(_backgroundTexture, _previewTexture);
        }

        public void Dispose()
        {
            if (_maskTexture != null)
            {
                _maskTexture.Release();
                _maskTexture = null;
            }

            if (_previewTexture != null)
            {
                _previewTexture.Release();
                _previewTexture = null;
            }

            if (_backgroundTexture != null)
            {
                Object.DestroyImmediate(_backgroundTexture);
                _backgroundTexture = null;
            }
        }

        /// <summary>
        /// CPU側バッファをGPU側テクスチャに同期
        /// </summary>
        public void SyncCpuToGpu()
        {
            if (_maskTexture == null || _maskValues == null)
                return;

            var texture = new Texture2D(_width, _height, TextureFormat.RGBA32, false);
            var pixels = new Color[_maskValues.Length];

            for (int i = 0; i < _maskValues.Length; i++)
            {
                float value = _maskValues[i];
                // グレースケール値として全チャンネルに設定
                pixels[i] = new Color(value, value, value, 1f);
            }

            texture.SetPixels(pixels);
            texture.Apply();

            Graphics.Blit(texture, _maskTexture);
            Object.DestroyImmediate(texture);
        }

        /// <summary>
        /// GPU側テクスチャをCPU側バッファに同期
        /// </summary>
        public void SyncGpuToCpu()
        {
            if (_maskTexture == null || _maskValues == null)
                return;

            var texture = new Texture2D(_width, _height, TextureFormat.RGBA32, false);
            RenderTexture.active = _maskTexture;
            texture.ReadPixels(new Rect(0, 0, _width, _height), 0, 0);
            texture.Apply();
            RenderTexture.active = null;

            var pixels = texture.GetPixels();
            for (int i = 0; i < _maskValues.Length; i++)
            {
                // グレースケール値として赤チャンネルを使用
                _maskValues[i] = pixels[i].r;
            }

            Object.DestroyImmediate(texture);
        }

        /// <summary>
        /// 指定座標のマスク値を取得
        /// </summary>
        public float GetValue(int x, int y)
        {
            if (x < 0 || x >= _width || y < 0 || y >= _height)
                return 0f;

            return _maskValues[y * _width + x];
        }

        /// <summary>
        /// 指定座標のマスク値を設定
        /// </summary>
        public void SetValue(int x, int y, float value)
        {
            if (x < 0 || x >= _width || y < 0 || y >= _height)
                return;

            _maskValues[y * _width + x] = Mathf.Clamp01(value);
        }

        /// <summary>
        /// 全マスク値をクリア（白 = 1.0で埋める）
        /// </summary>
        public void Clear()
        {
            for (int i = 0; i < _maskValues.Length; i++)
            {
                _maskValues[i] = 1.0f;
            }

            SyncCpuToGpu();
        }

        /// <summary>
        /// マスク値を反転（0→1, 1→0）
        /// </summary>
        public void Invert()
        {
            SyncGpuToCpu();

            for (int i = 0; i < _maskValues.Length; i++)
            {
                _maskValues[i] = 1f - _maskValues[i];
            }

            SyncCpuToGpu();
        }

        /// <summary>
        /// 現在の状態のコピーを取得（Undo用）
        /// </summary>
        public float[] GetSnapshot()
        {
            var snapshot = new float[_maskValues.Length];
            System.Array.Copy(_maskValues, snapshot, _maskValues.Length);
            return snapshot;
        }

        /// <summary>
        /// 状態を復元（Undo/Redo用）
        /// </summary>
        public void RestoreSnapshot(float[] snapshot)
        {
            if (snapshot == null || snapshot.Length != _maskValues.Length)
                return;

            System.Array.Copy(snapshot, _maskValues, _maskValues.Length);
            SyncCpuToGpu();
        }
    }
}
