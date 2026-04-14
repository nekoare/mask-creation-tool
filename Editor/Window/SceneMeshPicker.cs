using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace NekoareMaskTool.Editor
{
    /// <summary>
    /// Scene上のメッシュに対するレイキャスト・スクリーン半径内triangle列挙・可視面判定を提供する。
    /// ストローク開始時に BakeMesh + 一時 MeshCollider を構築し、ストローク終了時に破棄する。
    /// </summary>
    public class SceneMeshPicker
    {
        private GameObject _colliderHost;
        private Mesh _bakedMesh;
        private MeshCollider _meshCollider;
        private Renderer _targetRenderer;
        private Matrix4x4 _localToWorld;
        private int _materialIndex;
        private int[] _submeshTriangles;
        private Vector3[] _bakedVertices;

        // BeginPicking時に1回だけ計算してキャッシュする（ストローク中の高速化）
        private Vector3[] _triCentroidsWS;  // submesh内 triangle 数ぶん
        private Vector3[] _triNormalsWS;
        private Vector3[] _triVertsWS;      // submesh内 triangle数 * 3 個
        private int[] _allTrianglesCache;   // baked mesh 全 triangle 配列のキャッシュ（occlusion比較用）

        public bool IsActive => _meshCollider != null;

        /// <summary>
        /// レイキャスト結果
        /// </summary>
        public struct PickResult
        {
            public int triangleIndex;   // baked mesh の triangles 配列上の triangle index (0始まりの三角形番号)
            public Vector3 worldPoint;
            public Vector2 uv;
        }

        /// <summary>
        /// レイキャスト用の状態を構築する。ストローク開始時に1回呼ぶ。
        /// 既存の状態がある場合は破棄してから再構築する。
        /// </summary>
        public void BeginPicking(Renderer renderer, int materialIndex)
        {
            EndPicking();

            if (renderer == null) return;
            _targetRenderer = renderer;
            _materialIndex = materialIndex;
            _localToWorld = renderer.transform.localToWorldMatrix;

            _bakedMesh = new Mesh { name = "_NekoareMaskTool_PickerMesh" };
            _bakedMesh.hideFlags = HideFlags.HideAndDontSave;

            // SkinnedMeshRenderer はスキニング済みメッシュを取得、それ以外は MeshFilter のメッシュを使う
            if (renderer is SkinnedMeshRenderer smr)
            {
                smr.BakeMesh(_bakedMesh);
            }
            else
            {
                var mf = renderer.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null)
                {
                    Object.DestroyImmediate(_bakedMesh);
                    _bakedMesh = null;
                    return;
                }
                // sharedMesh をそのまま使うと書き換え時に元アセットを破壊するため、コピーを作る
                var src = mf.sharedMesh;
                _bakedMesh.vertices = src.vertices;
                _bakedMesh.normals = src.normals;
                _bakedMesh.uv = src.uv;
                _bakedMesh.triangles = src.triangles;
                _bakedMesh.subMeshCount = src.subMeshCount;
                for (int i = 0; i < src.subMeshCount; i++)
                {
                    _bakedMesh.SetTriangles(src.GetTriangles(i), i);
                }
            }

            _bakedVertices = _bakedMesh.vertices;
            if (materialIndex >= 0 && materialIndex < _bakedMesh.subMeshCount)
            {
                _submeshTriangles = _bakedMesh.GetTriangles(materialIndex);
            }
            else
            {
                _submeshTriangles = _bakedMesh.triangles;
            }
            _allTrianglesCache = _bakedMesh.triangles;

            // submesh内triangleのworld頂点・重心・法線を事前計算
            int triCount = _submeshTriangles.Length / 3;
            _triCentroidsWS = new Vector3[triCount];
            _triNormalsWS = new Vector3[triCount];
            _triVertsWS = new Vector3[triCount * 3];
            for (int t = 0; t < triCount; t++)
            {
                int t3 = t * 3;
                Vector3 v0 = _localToWorld.MultiplyPoint3x4(_bakedVertices[_submeshTriangles[t3]]);
                Vector3 v1 = _localToWorld.MultiplyPoint3x4(_bakedVertices[_submeshTriangles[t3 + 1]]);
                Vector3 v2 = _localToWorld.MultiplyPoint3x4(_bakedVertices[_submeshTriangles[t3 + 2]]);
                _triVertsWS[t3] = v0;
                _triVertsWS[t3 + 1] = v1;
                _triVertsWS[t3 + 2] = v2;
                _triCentroidsWS[t] = (v0 + v1 + v2) * (1f / 3f);
                _triNormalsWS[t] = Vector3.Cross(v1 - v0, v2 - v0).normalized;
            }

            // 一時GameObjectにMeshColliderをアタッチ。renderer の transform に同期する
            _colliderHost = new GameObject("_NekoareMaskTool_PickerCollider");
            _colliderHost.hideFlags = HideFlags.HideAndDontSave;
            _colliderHost.transform.SetPositionAndRotation(renderer.transform.position, renderer.transform.rotation);
            _colliderHost.transform.localScale = renderer.transform.lossyScale;
            _meshCollider = _colliderHost.AddComponent<MeshCollider>();
            _meshCollider.sharedMesh = _bakedMesh;
        }

        /// <summary>
        /// ピッキング状態を破棄する。ストローク終了時に呼ぶ。
        /// </summary>
        public void EndPicking()
        {
            if (_colliderHost != null)
            {
                Object.DestroyImmediate(_colliderHost);
                _colliderHost = null;
            }
            if (_bakedMesh != null)
            {
                Object.DestroyImmediate(_bakedMesh);
                _bakedMesh = null;
            }
            _meshCollider = null;
            _bakedVertices = null;
            _submeshTriangles = null;
            _triCentroidsWS = null;
            _triNormalsWS = null;
            _triVertsWS = null;
            _allTrianglesCache = null;
            _targetRenderer = null;
        }

        /// <summary>
        /// マウス位置から最も手前のtriangleをレイキャストで取得する
        /// </summary>
        public bool TryPick(Vector2 mousePos, Camera camera, out PickResult result)
        {
            result = default;
            if (_meshCollider == null || camera == null) return false;

            // SceneView のマウス座標系（Y軸下向き）からカメラ用の座標系（Y軸上向き）に変換
            var ray = HandleUtility.GUIPointToWorldRay(mousePos);
            if (!_meshCollider.Raycast(ray, out var hit, float.MaxValue)) return false;

            result = new PickResult
            {
                triangleIndex = hit.triangleIndex,
                worldPoint = hit.point,
                uv = hit.textureCoord
            };
            return true;
        }

        /// <summary>
        /// マウス位置を中心にスクリーン半径(px)以内に重心がある triangle を列挙する。
        /// 対象は materialIndex のSubMeshに限定される。
        /// 各 triangle について可視面判定（backface + occlusion）も行い、可視のものだけ返す。
        /// </summary>
        public List<int> EnumerateVisibleTrianglesInScreenRadius(Vector2 mousePos, Camera camera, float radiusPx)
        {
            var result = new List<int>();
            if (_meshCollider == null || camera == null || _triCentroidsWS == null) return result;

            float radiusSqr = radiusPx * radiusPx;
            Vector3 cameraPos = camera.transform.position;
            Vector2 mouseScreen = HandleUtility.GUIPointToScreenPixelCoordinate(mousePos);

            // マウス位置の最前面 hit を基準深度とし、近い深度の triangle を可視とみなす。
            var referenceRay = HandleUtility.GUIPointToWorldRay(mousePos);
            bool hasReference = _meshCollider.Raycast(referenceRay, out var referenceHit, float.MaxValue);
            float depthTolerance = hasReference ? Mathf.Max(0.02f, referenceHit.distance * 0.05f) : 0f;

            // ヒット三角形を別ルートで含めるための submesh-index 検索
            int hitTriSubmeshNumber = -1;
            if (hasReference)
            {
                int hitGlobalStart = referenceHit.triangleIndex * 3;
                if (hitGlobalStart + 2 < _allTrianglesCache.Length)
                {
                    int v0 = _allTrianglesCache[hitGlobalStart];
                    int v1 = _allTrianglesCache[hitGlobalStart + 1];
                    int v2 = _allTrianglesCache[hitGlobalStart + 2];
                    for (int t = 0; t < _submeshTriangles.Length / 3; t++)
                    {
                        int t3 = t * 3;
                        if (_submeshTriangles[t3] == v0 && _submeshTriangles[t3 + 1] == v1 && _submeshTriangles[t3 + 2] == v2)
                        {
                            hitTriSubmeshNumber = t;
                            break;
                        }
                    }
                }
            }

            int triCount = _triCentroidsWS.Length;
            for (int t = 0; t < triCount; t++)
            {
                // ヒット三角形は raycast で確実に可視なのでフィルタを全てバイパスする
                bool isHit = (t == hitTriSubmeshNumber);
                Vector3 centroid = _triCentroidsWS[t];

                if (!isHit)
                {
                    // 1. スクリーン半径フィルタ
                    Vector3 sp = camera.WorldToScreenPoint(centroid);
                    if (sp.z < 0) continue;
                    float dx = sp.x - mouseScreen.x;
                    float dy = sp.y - mouseScreen.y;
                    if (dx * dx + dy * dy > radiusSqr) continue;

                    // 2. Backface culling — 完全に向こう向き(<0)のみ除外。
                    // edge-on (=0) や法線が退化(=0)の triangle も含めることで、
                    // 顔の口角・まぶたなど grazing angle の領域でも候補が拾えるようにする。
                    Vector3 toCam = cameraPos - centroid;
                    float ndotc = Vector3.Dot(_triNormalsWS[t], toCam);
                    if (ndotc < 0f) continue;

                    // 3. 深度ベースの occlusion
                    if (hasReference)
                    {
                        float candidateDistance = toCam.magnitude;
                        float diff = Mathf.Abs(candidateDistance - referenceHit.distance);
                        if (diff > depthTolerance) continue;
                    }
                }

                result.Add(t);
            }
            return result;
        }

        /// <summary>
        /// SubMesh内のtriangle番号から3つのUV座標を取得する
        /// </summary>
        public bool TryGetTriangleUVs(int submeshTriangleNumber, out Vector2 uv0, out Vector2 uv1, out Vector2 uv2)
        {
            uv0 = uv1 = uv2 = default;
            if (_bakedMesh == null || _submeshTriangles == null) return false;
            int t = submeshTriangleNumber * 3;
            if (t + 2 >= _submeshTriangles.Length) return false;

            var uvs = _bakedMesh.uv;
            if (uvs == null || uvs.Length == 0) return false;

            uv0 = uvs[_submeshTriangles[t]];
            uv1 = uvs[_submeshTriangles[t + 1]];
            uv2 = uvs[_submeshTriangles[t + 2]];
            return true;
        }

        /// <summary>
        /// SubMesh内のtriangle番号から world 空間の頂点座標を取得する（円形クリップ用）
        /// </summary>
        public bool TryGetTriangleWorldVerts(int submeshTriangleNumber, out Vector3 v0, out Vector3 v1, out Vector3 v2)
        {
            v0 = v1 = v2 = default;
            if (_triVertsWS == null) return false;
            int t = submeshTriangleNumber * 3;
            if (t + 2 >= _triVertsWS.Length) return false;
            v0 = _triVertsWS[t];
            v1 = _triVertsWS[t + 1];
            v2 = _triVertsWS[t + 2];
            return true;
        }
    }
}
