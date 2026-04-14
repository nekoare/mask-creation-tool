using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;

namespace NekoareMaskTool.Editor
{
    /// <summary>
    /// UVアイランド選択機能（ComputeShaderベースのラスタライズに変更）
    /// </summary>
    public class IslandSelector
    {
        private Mesh _mesh;
        private int _submeshIndex; // 対象サブメッシュインデックス
        private int[] _submeshTriangles; // 対象サブメッシュの三角形インデックス
        private List<List<int>> _islands; // アイランドごとの頂点インデックスリスト
        private int _textureWidth;
        private int _textureHeight;

        // ComputeShader関連
        private ComputeShader _rasterShader;
        private RenderTexture _selectAreaRT;
        private RenderTexture _clipMaskRT; // はみ出し防止用（選択ツールと独立）

        public void Initialize(Mesh mesh, int textureWidth, int textureHeight, int submeshIndex = 0)
        {
            _mesh = mesh;
            _textureWidth = textureWidth;
            _textureHeight = textureHeight;

            // サブメッシュインデックスを保存し、対象サブメッシュの三角形を取得
            _submeshIndex = submeshIndex;
            if (_mesh != null && submeshIndex >= 0 && submeshIndex < _mesh.subMeshCount)
            {
                _submeshTriangles = _mesh.GetTriangles(submeshIndex);
            }
            else if (_mesh != null)
            {
                _submeshTriangles = _mesh.triangles;
            }
            else
            {
                _submeshTriangles = new int[0];
            }

            _islands = DetectIslands();

            // ComputeShaderを読み込み（パスに依存しないGUID検索）
            var guids = AssetDatabase.FindAssets("NekoareMaskToolSelectArea t:ComputeShader");
            if (guids.Length > 0)
            {
                var path = AssetDatabase.GUIDToAssetPath(guids[0]);
                _rasterShader = AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
            }

            // RTの初期化
            if (_selectAreaRT != null)
            {
                _selectAreaRT.Release();
                _selectAreaRT = null;
            }

            _selectAreaRT = new RenderTexture(_textureWidth, _textureHeight, 0, RenderTextureFormat.ARGB32)
            {
                enableRandomWrite = true
            };
            _selectAreaRT.Create();
        }

        public void Dispose()
        {
            if (_selectAreaRT != null)
            {
                _selectAreaRT.Release();
                _selectAreaRT = null;
            }
            if (_clipMaskRT != null)
            {
                _clipMaskRT.Release();
                _clipMaskRT = null;
            }
        }

        /// <summary>
        /// 全UVアイランドをラスタライズしたRenderTextureを取得（はみ出し防止用クリッピングマスク）
        /// UV三角形が存在するピクセル=白(1)、存在しないピクセル=黒(0)
        /// </summary>
        public RenderTexture GetClippingMask()
        {
            if (_mesh == null || _rasterShader == null)
                return null;

            // クリッピングマスク専用RTを作成（選択ツールの_selectAreaRTとは独立）
            if (_clipMaskRT == null)
            {
                _clipMaskRT = new RenderTexture(_textureWidth, _textureHeight, 0, RenderTextureFormat.ARGB32)
                {
                    enableRandomWrite = true
                };
                _clipMaskRT.Create();
            }
            else if (!_clipMaskRT.IsCreated())
            {
                _clipMaskRT.Create();
            }

            // 対象サブメッシュの三角形のUV座標を収集
            var triangles = _submeshTriangles;
            var uvs = new List<Vector2>();
            _mesh.GetUVs(0, uvs);

            if (uvs.Count == 0 || triangles == null || triangles.Length == 0)
                return null;

            var triList = new List<Vector4>();
            for (int i = 0; i < triangles.Length; i += 3)
            {
                var uvA = uvs[triangles[i]];
                var uvB = uvs[triangles[i + 1]];
                var uvC = uvs[triangles[i + 2]];

                triList.Add(new Vector4(uvA.x * _textureWidth, uvA.y * _textureHeight, 0, 0));
                triList.Add(new Vector4(uvB.x * _textureWidth, uvB.y * _textureHeight, 0, 0));
                triList.Add(new Vector4(uvC.x * _textureWidth, uvC.y * _textureHeight, 0, 0));
            }

            var triBuffer = new ComputeBuffer(triList.Count, sizeof(float) * 4);
            triBuffer.SetData(triList.ToArray());

            var resultCount = _textureWidth * _textureHeight;
            var resultBuffer = new ComputeBuffer(resultCount, sizeof(int));
            resultBuffer.SetData(new int[resultCount]);

            int kernel = _rasterShader.FindKernel("CSRasterize");
            _rasterShader.SetBuffer(kernel, "Triangles", triBuffer);
            _rasterShader.SetInt("TriangleCount", triList.Count / 3);
            _rasterShader.SetInt("Width", _textureWidth);
            _rasterShader.SetInt("Height", _textureHeight);
            _rasterShader.SetBuffer(kernel, "Result", resultBuffer);
            _rasterShader.SetTexture(kernel, "SelectAreaTex", _clipMaskRT);

            int groupsX = Mathf.CeilToInt(_textureWidth / 32.0f);
            int groupsY = Mathf.CeilToInt(_textureHeight / 32.0f);
            _rasterShader.Dispatch(kernel, groupsX, groupsY, 1);

            // Rasterizerの精度限界で内部エッジに沿って1pxの穴が残るメッシュがあるため
            // モルフォロジー クローズ（膨張→収縮）で内側の細かな穴を埋める
            ApplyMorphologicalClose(resultBuffer, _clipMaskRT, 1);

            triBuffer.Dispose();
            resultBuffer.Dispose();

            return _clipMaskRT;
        }

        /// <summary>
        /// UVエッジ共有で連結された三角形群（UVチャート）を BFS で求める。
        /// 頂点 index ではなく UV 座標で比較するため、同じ頂点でも別 UV なら別チャート扱いになる。
        /// </summary>
        private List<int> GetUVChartTriangles(int startTriNumber, int[] triangles, List<Vector2> uvs)
        {
            var result = new List<int>();
            int triCount = triangles.Length / 3;
            if (startTriNumber < 0 || startTriNumber >= triCount) return result;

            // UVエッジ→三角形インデックスの逆引きマップを構築
            // 浮動小数点UVを整数キー化（1e-5 精度）
            var edgeMap = new Dictionary<long, List<int>>(triCount * 3);
            for (int t = 0; t < triCount; t++)
            {
                int t3 = t * 3;
                Vector2 uvA = uvs[triangles[t3]];
                Vector2 uvB = uvs[triangles[t3 + 1]];
                Vector2 uvC = uvs[triangles[t3 + 2]];
                AddEdge(edgeMap, uvA, uvB, t);
                AddEdge(edgeMap, uvB, uvC, t);
                AddEdge(edgeMap, uvC, uvA, t);
            }

            // BFS
            var visited = new HashSet<int> { startTriNumber };
            result.Add(startTriNumber);
            var queue = new Queue<int>();
            queue.Enqueue(startTriNumber);
            while (queue.Count > 0)
            {
                int t = queue.Dequeue();
                int t3 = t * 3;
                Vector2 uvA = uvs[triangles[t3]];
                Vector2 uvB = uvs[triangles[t3 + 1]];
                Vector2 uvC = uvs[triangles[t3 + 2]];
                EnqueueNeighbors(edgeMap, uvA, uvB, t, visited, result, queue);
                EnqueueNeighbors(edgeMap, uvB, uvC, t, visited, result, queue);
                EnqueueNeighbors(edgeMap, uvC, uvA, t, visited, result, queue);
            }
            return result;
        }

        private const float UV_QUANT = 100000f;

        private static long QuantizeUv(Vector2 uv)
        {
            // UVを10万倍して int に丸め、2つを32bit ずつ詰めて 64bit キーにする
            int qx = Mathf.RoundToInt(uv.x * UV_QUANT);
            int qy = Mathf.RoundToInt(uv.y * UV_QUANT);
            return ((long)(uint)qx << 32) | (uint)qy;
        }

        private static long EdgeKey(Vector2 a, Vector2 b)
        {
            long ka = QuantizeUv(a);
            long kb = QuantizeUv(b);
            // 順序に依存しないキー
            long lo = ka < kb ? ka : kb;
            long hi = ka < kb ? kb : ka;
            // 32bit hash combine
            unchecked
            {
                return lo * 1099511628211L ^ hi;
            }
        }

        private static void AddEdge(Dictionary<long, List<int>> edgeMap, Vector2 a, Vector2 b, int triIdx)
        {
            long k = EdgeKey(a, b);
            if (!edgeMap.TryGetValue(k, out var list))
            {
                list = new List<int>(2);
                edgeMap[k] = list;
            }
            list.Add(triIdx);
        }

        private static void EnqueueNeighbors(Dictionary<long, List<int>> edgeMap, Vector2 a, Vector2 b,
            int self, HashSet<int> visited, List<int> result, Queue<int> queue)
        {
            if (!edgeMap.TryGetValue(EdgeKey(a, b), out var list)) return;
            foreach (var nb in list)
            {
                if (nb == self || visited.Contains(nb)) continue;
                visited.Add(nb);
                result.Add(nb);
                queue.Enqueue(nb);
            }
        }

        /// <summary>
        /// results[] の中で、start 座標から 4連結で到達可能なピクセルだけを残し、
        /// それ以外は 0 にする。安全網として UVチャート filter の後に追加の連結成分抽出を行う。
        /// </summary>
        private void RestrictToConnectedComponent(int[] results, Vector2Int start)
        {
            int w = _textureWidth;
            int h = _textureHeight;
            if (start.x < 0 || start.x >= w || start.y < 0 || start.y >= h) return;
            int startIdx = start.y * w + start.x;
            if (results[startIdx] == 0) return;  // クリック位置が対象外ならそのまま

            var visited = new bool[results.Length];
            var queue = new Queue<Vector2Int>();
            queue.Enqueue(start);
            visited[startIdx] = true;

            // 4連結で BFS（対角経由で細いUVストライプに伝播しないように）
            int[] dx4 = { -1, 1, 0, 0 };
            int[] dy4 = { 0, 0, -1, 1 };
            while (queue.Count > 0)
            {
                var p = queue.Dequeue();
                for (int d = 0; d < 4; d++)
                {
                    int nx = p.x + dx4[d];
                    int ny = p.y + dy4[d];
                    if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                    int nIdx = ny * w + nx;
                    if (visited[nIdx] || results[nIdx] == 0) continue;
                    visited[nIdx] = true;
                    queue.Enqueue(new Vector2Int(nx, ny));
                }
            }

            // 到達しなかった Result=1 を 0 に落とす
            for (int i = 0; i < results.Length; i++)
            {
                if (results[i] != 0 && !visited[i]) results[i] = 0;
            }
        }

        /// <summary>
        /// モルフォロジー オープン（収縮→膨張）: 細い構造（ストライプなど）を削除し、
        /// 太い本体は保持する。margin が 2 なら 2px 以下の幅の構造を消せる。
        /// </summary>
        private void ApplyMorphologicalOpen(ComputeBuffer resultBuffer, RenderTexture tex, int margin)
        {
            if (_rasterShader == null || margin <= 0) return;
            int resultCount = _textureWidth * _textureHeight;
            var tempBuffer = new ComputeBuffer(resultCount, sizeof(int));
            tempBuffer.SetData(new int[resultCount]);

            int groupsX = Mathf.CeilToInt(_textureWidth / 32.0f);
            int groupsY = Mathf.CeilToInt(_textureHeight / 32.0f);

            int kExpand = _rasterShader.FindKernel("CSExpand");
            int kErode = _rasterShader.FindKernel("CSErode");

            _rasterShader.SetInt("Width", _textureWidth);
            _rasterShader.SetInt("Height", _textureHeight);
            _rasterShader.SetInt("ExpansionMargin", margin);

            // 収縮: resultBuffer → tempBuffer
            _rasterShader.SetBuffer(kErode, "Result", resultBuffer);
            _rasterShader.SetBuffer(kErode, "ResultOut", tempBuffer);
            _rasterShader.SetTexture(kErode, "SelectAreaTex", tex);
            _rasterShader.Dispatch(kErode, groupsX, groupsY, 1);

            // 膨張: tempBuffer → resultBuffer
            _rasterShader.SetBuffer(kExpand, "Result", tempBuffer);
            _rasterShader.SetBuffer(kExpand, "ResultOut", resultBuffer);
            _rasterShader.SetTexture(kExpand, "SelectAreaTex", tex);
            _rasterShader.Dispatch(kExpand, groupsX, groupsY, 1);

            tempBuffer.Dispose();
        }

        /// <summary>
        /// Resultバッファに対してモルフォロジー クローズ（膨張→収縮）を適用し、
        /// 同時に SelectAreaTex 側の可視表現も最終形にそろえる。
        /// 内部ラスタライズの 1px 穴を閉じる用途。
        /// </summary>
        private void ApplyMorphologicalClose(ComputeBuffer resultBuffer, RenderTexture tex, int margin)
        {
            if (_rasterShader == null || margin <= 0) return;
            int resultCount = _textureWidth * _textureHeight;
            var tempBuffer = new ComputeBuffer(resultCount, sizeof(int));
            tempBuffer.SetData(new int[resultCount]);

            int groupsX = Mathf.CeilToInt(_textureWidth / 32.0f);
            int groupsY = Mathf.CeilToInt(_textureHeight / 32.0f);

            int kExpand = _rasterShader.FindKernel("CSExpand");
            int kErode = _rasterShader.FindKernel("CSErode");

            _rasterShader.SetInt("Width", _textureWidth);
            _rasterShader.SetInt("Height", _textureHeight);
            _rasterShader.SetInt("ExpansionMargin", margin);

            // 膨張: resultBuffer → tempBuffer
            _rasterShader.SetBuffer(kExpand, "Result", resultBuffer);
            _rasterShader.SetBuffer(kExpand, "ResultOut", tempBuffer);
            _rasterShader.SetTexture(kExpand, "SelectAreaTex", tex);
            _rasterShader.Dispatch(kExpand, groupsX, groupsY, 1);

            // 収縮: tempBuffer → resultBuffer
            _rasterShader.SetBuffer(kErode, "Result", tempBuffer);
            _rasterShader.SetBuffer(kErode, "ResultOut", resultBuffer);
            _rasterShader.SetTexture(kErode, "SelectAreaTex", tex);
            _rasterShader.Dispatch(kErode, groupsX, groupsY, 1);

            tempBuffer.Dispose();
        }

        /// <summary>
        /// 指定テクスチャ座標が含まれるアイランドの全ピクセルを取得（精密ラスタライズ）
        /// </summary>
        /// <summary>
        /// 指定 texCoord に該当する island の submesh内 triangle 番号リストを返す（ホバープレビュー描画用）。
        /// </summary>
        public List<int> GetIslandTriangleNumbers(Vector2Int texCoord)
        {
            var result = new List<int>();
            if (_mesh == null || _islands == null || _submeshTriangles == null) return result;

            var uv = new Vector2(
                texCoord.x / (float)_textureWidth,
                texCoord.y / (float)_textureHeight
            );
            int triangleIndex = FindTriangleAtUV(uv);
            if (triangleIndex == -1) return result;
            int islandIndex = GetIslandIndexForTriangle(triangleIndex);
            if (islandIndex == -1) return result;

            var vertexSet = new HashSet<int>(_islands[islandIndex]);
            var triangles = _submeshTriangles;
            for (int i = 0; i < triangles.Length; i += 3)
            {
                int a = triangles[i];
                int b = triangles[i + 1];
                int c = triangles[i + 2];
                if (vertexSet.Contains(a) && vertexSet.Contains(b) && vertexSet.Contains(c))
                {
                    result.Add(i / 3);
                }
            }
            return result;
        }

        public List<Vector2Int> GetIslandPixels(Vector2Int texCoord, int expansionMargin = 0)
        {
            if (_mesh == null || _islands == null)
                return new List<Vector2Int>();

            // RenderTextureがGPUから解放されている場合は再作成
            if (_selectAreaRT != null && !_selectAreaRT.IsCreated())
            {
                _selectAreaRT.Create();
            }

            // ComputeShaderが無効な場合は早期リターン
            if (_rasterShader == null || _selectAreaRT == null)
                return new List<Vector2Int>();

            // UV座標に変換
            // texCoordはWindowPosToTexturePosからのY-up座標（y=0が下、y=heightが上）
            // UVもY-up（y=0が下、y=1が上）なので反転不要
            var uv = new Vector2(
                texCoord.x / (float)_textureWidth,
                texCoord.y / (float)_textureHeight
            );

            // クリック位置を含む三角形を検索
            int triangleIndex = FindTriangleAtUV(uv);
            if (triangleIndex == -1)
                return new List<Vector2Int>();

            // UVチャート連結成分を取得（頂点接続ではなく UV エッジ共有で連結）
            var triangles = _submeshTriangles;
            var uvs = new List<Vector2>();
            _mesh.GetUVs(0, uvs);
            var uvChartTriangles = GetUVChartTriangles(triangleIndex, triangles, uvs);
            var triList = new List<Vector4>(); // 3 Vector4 per triangle (x,y)

            foreach (var triIdx in uvChartTriangles)
            {
                int ti = triIdx * 3;
                int a = triangles[ti];
                int b = triangles[ti + 1];
                int c = triangles[ti + 2];
                {
                    var uvA = uvs[a];
                    var uvB = uvs[b];
                    var uvC = uvs[c];

                    // UV -> テクスチャ座標（ピクセル）
                    // UVもピクセル座標もY-up convention（y=0が下）で統一
                    triList.Add(new Vector4(uvA.x * _textureWidth, uvA.y * _textureHeight, 0, 0));
                    triList.Add(new Vector4(uvB.x * _textureWidth, uvB.y * _textureHeight, 0, 0));
                    triList.Add(new Vector4(uvC.x * _textureWidth, uvC.y * _textureHeight, 0, 0));
                }
            }

            if (triList.Count == 0)
            {
                return new List<Vector2Int>();
            }

            // ComputeBufferに転送してラスタライズ
            var triBuffer = new ComputeBuffer(triList.Count, sizeof(float) * 4);
            triBuffer.SetData(triList.ToArray());

            var resultCount = _textureWidth * _textureHeight;
            var resultBuffer = new ComputeBuffer(resultCount, sizeof(int));
            var zero = new int[resultCount];
            resultBuffer.SetData(zero);

            int kernel = _rasterShader.FindKernel("CSRasterize");
            _rasterShader.SetBuffer(kernel, "Triangles", triBuffer);
            _rasterShader.SetInt("TriangleCount", triList.Count / 3);
            _rasterShader.SetInt("Width", _textureWidth);
            _rasterShader.SetInt("Height", _textureHeight);
            _rasterShader.SetBuffer(kernel, "Result", resultBuffer);
            _rasterShader.SetTexture(kernel, "SelectAreaTex", _selectAreaRT);

            int groupsX = Mathf.CeilToInt(_textureWidth / 32.0f);
            int groupsY = Mathf.CeilToInt(_textureHeight / 32.0f);
            _rasterShader.Dispatch(kernel, groupsX, groupsY, 1);

            // Rasterizerの精度限界で内部エッジに沿って1pxの穴が残るメッシュがあるため
            // モルフォロジー クローズで内側の細かな穴を埋める
            ApplyMorphologicalClose(resultBuffer, _selectAreaRT, 1);
            // 細いストライプ状UV（縫い目など）を削って本体だけにする
            ApplyMorphologicalOpen(resultBuffer, _selectAreaRT, 2);

            // 結果を取得（必要ならGPUで拡張/収縮を実行）
            var results = new int[resultCount];

            if (expansionMargin != 0)
            {
                var resultOutBuffer = new ComputeBuffer(resultCount, sizeof(int));
                var zeroOut = new int[resultCount];
                resultOutBuffer.SetData(zeroOut);

                // 正: CSExpand（膨張）、負: CSErode（収縮）
                string kernelName = expansionMargin > 0 ? "CSExpand" : "CSErode";
                int marginKernel = _rasterShader.FindKernel(kernelName);
                _rasterShader.SetBuffer(marginKernel, "Result", resultBuffer);
                _rasterShader.SetBuffer(marginKernel, "ResultOut", resultOutBuffer);
                _rasterShader.SetTexture(marginKernel, "SelectAreaTex", _selectAreaRT);
                _rasterShader.SetInt("Width", _textureWidth);
                _rasterShader.SetInt("Height", _textureHeight);
                _rasterShader.SetInt("ExpansionMargin", Mathf.Abs(expansionMargin));

                int gX = Mathf.CeilToInt(_textureWidth / 32.0f);
                int gY = Mathf.CeilToInt(_textureHeight / 32.0f);
                _rasterShader.Dispatch(marginKernel, gX, gY, 1);

                resultOutBuffer.GetData(results);
                resultOutBuffer.Dispose();
            }
            else
            {
                resultBuffer.GetData(results);
            }

            // トポロジー島に属する全triangleのラスタライズ結果は、UV seam で分かれた
            // 複数のUVチャートを含む。クリックされたUV位置から 8連結で到達可能な連結成分だけに絞る
            // ことで、「画面で見えている塊」だけを選択対象にする。
            RestrictToConnectedComponent(results, texCoord);

            // ピクセルリストを作成
            var pixels = new List<Vector2Int>();
            for (int y = 0; y < _textureHeight; y++)
            {
                for (int x = 0; x < _textureWidth; x++)
                {
                    int idx = y * _textureWidth + x;
                    if (results[idx] != 0)
                        pixels.Add(new Vector2Int(x, y));
                }
            }

            // 後処理
            triBuffer.Dispose();
            resultBuffer.Dispose();

            return pixels;
        }



        private List<List<int>> DetectIslands()
        {
            if (_mesh == null || _submeshTriangles == null || _submeshTriangles.Length == 0)
                return new List<List<int>>();

            var uvs = new List<Vector2>();
            _mesh.GetUVs(0, uvs);

            if (uvs.Count == 0)
                return new List<List<int>>();

            // サブメッシュの三角形のみを使用
            var triangles = _submeshTriangles;

            // Union-Find構造を初期化
            var parent = new int[uvs.Count];
            for (int i = 0; i < parent.Length; i++)
            {
                parent[i] = i;
            }

            // 三角形のエッジを処理してUV頂点を結合
            for (int i = 0; i < triangles.Length; i += 3)
            {
                int v0 = triangles[i];
                int v1 = triangles[i + 1];
                int v2 = triangles[i + 2];

                Union(parent, v0, v1);
                Union(parent, v1, v2);
                Union(parent, v2, v0);
            }

            // サブメッシュに含まれる頂点のみをグループ化
            var submeshVertices = new HashSet<int>();
            foreach (var idx in triangles)
            {
                submeshVertices.Add(idx);
            }

            // 連結成分をグループ化（サブメッシュの頂点のみ）
            var groups = new Dictionary<int, List<int>>();
            foreach (int i in submeshVertices)
            {
                int root = Find(parent, i);
                if (!groups.ContainsKey(root))
                {
                    groups[root] = new List<int>();
                }
                groups[root].Add(i);
            }

            return new List<List<int>>(groups.Values);
        }

        private int Find(int[] parent, int x)
        {
            if (parent[x] != x)
            {
                parent[x] = Find(parent, parent[x]);
            }
            return parent[x];
        }

        private void Union(int[] parent, int x, int y)
        {
            int rootX = Find(parent, x);
            int rootY = Find(parent, y);
            if (rootX != rootY)
            {
                parent[rootY] = rootX;
            }
        }

        private int FindTriangleAtUV(Vector2 uv)
        {
            var uvs = new List<Vector2>();
            _mesh.GetUVs(0, uvs);

            // サブメッシュの三角形のみを検索
            var triangles = _submeshTriangles;
            if (triangles == null || triangles.Length == 0)
                return -1;

            for (int i = 0; i < triangles.Length; i += 3)
            {
                var uv0 = uvs[triangles[i]];
                var uv1 = uvs[triangles[i + 1]];
                var uv2 = uvs[triangles[i + 2]];

                if (IsPointInTriangle(uv, uv0, uv1, uv2))
                {
                    return i / 3;
                }
            }

            return -1;
        }

        private bool IsPointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = Sign(p, a, b);
            float d2 = Sign(p, b, c);
            float d3 = Sign(p, c, a);

            bool hasNeg = (d1 < 0) || (d2 < 0) || (d3 < 0);
            bool hasPos = (d1 > 0) || (d2 > 0) || (d3 > 0);

            return !(hasNeg && hasPos);
        }

        private float Sign(Vector2 p1, Vector2 p2, Vector2 p3)
        {
            return (p1.x - p3.x) * (p2.y - p3.y) - (p2.x - p3.x) * (p1.y - p3.y);
        }

        private int GetIslandIndexForTriangle(int triangleIndex)
        {
            // サブメッシュの三角形インデックスを使用
            var triangles = _submeshTriangles;
            if (triangles == null || triangleIndex * 3 >= triangles.Length)
                return -1;

            int vertexIndex = triangles[triangleIndex * 3];

            for (int i = 0; i < _islands.Count; i++)
            {
                if (_islands[i].Contains(vertexIndex))
                {
                    return i;
                }
            }

            return -1;
        }

        private Rect CalculateIslandBounds(int islandIndex)
        {
            if (islandIndex < 0 || islandIndex >= _islands.Count)
                return new Rect(0, 0, 0, 0);

            var uvs = new List<Vector2>();
            _mesh.GetUVs(0, uvs);

            var vertices = _islands[islandIndex];

            if (vertices.Count == 0)
                return new Rect(0, 0, 0, 0);

            float minX = float.MaxValue;
            float minY = float.MaxValue;
            float maxX = float.MinValue;
            float maxY = float.MinValue;

            foreach (var vertexIndex in vertices)
            {
                var uv = uvs[vertexIndex];
                minX = Mathf.Min(minX, uv.x);
                minY = Mathf.Min(minY, uv.y);
                maxX = Mathf.Max(maxX, uv.x);
                maxY = Mathf.Max(maxY, uv.y);
            }

            return new Rect(minX, minY, maxX - minX, maxY - minY);
        }

        private List<Vector2Int> GetPixelsInBounds(Rect uvBounds, int expansionMargin)
        {
            var pixels = new List<Vector2Int>();

            // UV座標をテクスチャ座標に変換（Y-up convention統一）
            int minX = Mathf.FloorToInt(uvBounds.xMin * _textureWidth) - expansionMargin;
            int minY = Mathf.FloorToInt(uvBounds.yMin * _textureHeight) - expansionMargin;
            int maxX = Mathf.CeilToInt(uvBounds.xMax * _textureWidth) + expansionMargin;
            int maxY = Mathf.CeilToInt(uvBounds.yMax * _textureHeight) + expansionMargin;

            // クランプ
            minX = Mathf.Max(0, minX);
            minY = Mathf.Max(0, minY);
            maxX = Mathf.Min(_textureWidth - 1, maxX);
            maxY = Mathf.Min(_textureHeight - 1, maxY);

            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    pixels.Add(new Vector2Int(x, y));
                }
            }

            return pixels;
        }
    }
}
