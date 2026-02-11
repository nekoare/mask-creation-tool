using System;
using System.Collections.Generic;
using UnityEngine;

namespace NekoareMaskTool.Editor
{
    /// <summary>
    /// 外部ツールからマスクツールを開くためのコンテキスト情報
    /// CHM固有の型は使用せず、汎用的なデータのみを保持する
    /// </summary>
    public class MaskToolExternalContext
    {
        /// <summary>
        /// ターゲットRenderer（UVマップ生成のフォールバック用）
        /// atlasMeshが設定されている場合はそちらが優先される
        /// </summary>
        public SkinnedMeshRenderer targetRenderer;

        /// <summary>
        /// UI上の「対象」として表示するGameObject（nullの場合はtargetRendererのGameObjectを使用）
        /// </summary>
        public GameObject targetGameObject;

        /// <summary>
        /// アトラス座標系のUVを持つメッシュ（UVワイヤーフレーム生成用）
        /// nullの場合はtargetRendererのメッシュを使用
        /// </summary>
        public Mesh atlasMesh;

        /// <summary>
        /// 元のマスク情報リスト（座標変換の入力）
        /// </summary>
        public List<SourceMaskEntry> sourceMasks;

        /// <summary>
        /// アトラスの解像度
        /// </summary>
        public int atlasResolution;

        /// <summary>
        /// マスクRenderTextureが準備された時に呼ばれるコールバック
        /// 外部ツール（CHM等）がこのRenderTextureをプレビューに反映する
        /// </summary>
        public Action<RenderTexture> onMaskTextureAvailable;

        /// <summary>
        /// マスクツール終了時のクリーンアップコールバック
        /// 外部ツールがプレビューのマスクを解除する
        /// </summary>
        public Action onMaskToolClosed;

        /// <summary>
        /// 統合前の元Rendererリスト（ツリー表示・マテリアル参照用）
        /// </summary>
        public List<Renderer> sourceRenderers;

        /// <summary>
        /// マスク出力時に指定スロットへ適用するコールバック
        /// 引数: (保存先パス, スロットプロパティ名)
        /// </summary>
        public Action<string, string> onMaskApplied;

        /// <summary>
        /// 出力先スロットが変更された時のコールバック
        /// プレビュースロットの切り替えに使用
        /// 引数: スロットプロパティ名
        /// </summary>
        public Action<string> onOutputSlotChanged;

        /// <summary>
        /// 現在編集中のマスクスロット名（出力先のデフォルト値として使用）
        /// </summary>
        public string currentMaskSlotName;
    }

    /// <summary>
    /// 元のRendererが持っていたマスク情報（1アイランド分）
    /// </summary>
    public class SourceMaskEntry
    {
        /// <summary>
        /// 元のマスクテクスチャ（nullならマスクなし＝白扱い）
        /// </summary>
        public Texture2D maskTexture;

        /// <summary>
        /// 元のメインカラーテクスチャ（アトラス背景の合成用、nullなら白扱い）
        /// </summary>
        public Texture2D mainTexture;

        /// <summary>
        /// 元のUV境界（テクスチャ内でのアイランドの範囲、0-1）
        /// </summary>
        public Rect originalBounds;

        /// <summary>
        /// アトラス上での配置位置（左下隅、0-1）
        /// </summary>
        public Vector2 atlasPosition;

        /// <summary>
        /// アトラス上でのサイズ（0-1）
        /// </summary>
        public Vector2 atlasScale;

        /// <summary>
        /// sourceRenderers内のインデックス（どのRendererのアイランドか）
        /// </summary>
        public int sourceRendererIndex;

        /// <summary>
        /// そのRenderer上のマテリアルインデックス（どのsubmesh/マテリアルか）
        /// </summary>
        public int sourceMaterialIndex;
    }
}
