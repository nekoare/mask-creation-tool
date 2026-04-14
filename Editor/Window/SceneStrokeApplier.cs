using System.Collections.Generic;
using UnityEngine;

namespace NekoareMaskTool.Editor
{
    /// <summary>
    /// Sceneクリックで列挙された triangle 群を MaskCanvasModel に塗り込むユーティリティ。
    /// CPU側でtriangleをラスタライズしてピクセル集合を作り、SetValue + SyncCpuToGpu で反映する。
    /// </summary>
    public static class SceneStrokeApplier
    {
        /// <summary>
        /// 三角形データ（UV + world座標 + 三角形番号）。円形クリップ用に world 頂点が必要。
        /// </summary>
        public struct TriangleData
        {
            public Vector2 uv0, uv1, uv2;
            public Vector3 v0WS, v1WS, v2WS;
        }

        /// <summary>
        /// 与えられた triangle 群を、スクリーン空間の円形ブラシ範囲内のテクセルだけ塗る。
        /// 各テクセルの世界座標を重心座標補間で求め、スクリーン投影して mouse からの距離をチェックする。
        /// </summary>
        public static int PaintTrianglesScreenCircle(
            MaskCanvasModel model,
            IList<TriangleData> triangles,
            Camera camera,
            Vector2 mouseScreenPx,
            float radiusPx,
            float value)
        {
            if (model == null || triangles == null || triangles.Count == 0 || camera == null) return 0;

            int width = model.Width;
            int height = model.Height;
            float radiusSqr = radiusPx * radiusPx;
            var pixels = new HashSet<int>();
            foreach (var tri in triangles)
            {
                RasterizeTriangleWithCircleClip(tri, width, height, camera, mouseScreenPx, radiusSqr, pixels);
            }
            ApplyPixels(model, pixels, value);
            return pixels.Count;
        }

        /// <summary>
        /// 三角形群から円形クリップで覆われたピクセル集合のみを返す（model は変更しない）。
        /// 補間ストロークで複数回累積したいときに使う。
        /// </summary>
        public static void GatherPixelsScreenCircle(
            IList<TriangleData> triangles,
            Camera camera,
            Vector2 mouseScreenPx,
            float radiusPx,
            int width, int height,
            HashSet<int> outPixels)
        {
            if (triangles == null || triangles.Count == 0 || camera == null || outPixels == null) return;
            float radiusSqr = radiusPx * radiusPx;
            foreach (var tri in triangles)
            {
                RasterizeTriangleWithCircleClip(tri, width, height, camera, mouseScreenPx, radiusSqr, outPixels);
            }
        }

        /// <summary>
        /// 累積したピクセル集合を一括で model に適用し、SyncCpuToGpu は1回だけ呼ぶ
        /// </summary>
        public static void ApplyPixels(MaskCanvasModel model, HashSet<int> pixels, float value)
        {
            if (model == null || pixels == null || pixels.Count == 0) return;
            int width = model.Width;
            float clamped = Mathf.Clamp01(value);
            foreach (var key in pixels)
            {
                int x = key % width;
                int y = key / width;
                model.SetValue(x, y, clamped);
            }
            model.SyncCpuToGpu();
        }

        /// <summary>
        /// 円形クリップなしで三角形 UV を塗りつぶす（旧バージョン、未使用なら削除可）
        /// </summary>
        public static void PaintTriangles(MaskCanvasModel model, IList<(Vector2 uv0, Vector2 uv1, Vector2 uv2)> triangles, float value)
        {
            if (model == null || triangles == null || triangles.Count == 0) return;

            int width = model.Width;
            int height = model.Height;
            var pixels = new HashSet<int>();
            foreach (var tri in triangles)
            {
                RasterizeTriangle(tri.uv0, tri.uv1, tri.uv2, width, height, pixels);
            }

            float clamped = Mathf.Clamp01(value);
            foreach (var key in pixels)
            {
                int x = key % width;
                int y = key / width;
                model.SetValue(x, y, clamped);
            }
            model.SyncCpuToGpu();
        }

        private static void RasterizeTriangleWithCircleClip(
            TriangleData tri, int width, int height,
            Camera camera, Vector2 mouseScreenPx, float radiusSqr,
            HashSet<int> outPixels)
        {
            Vector2 p0 = new Vector2(tri.uv0.x * width, tri.uv0.y * height);
            Vector2 p1 = new Vector2(tri.uv1.x * width, tri.uv1.y * height);
            Vector2 p2 = new Vector2(tri.uv2.x * width, tri.uv2.y * height);

            int xMin = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(p0.x, p1.x, p2.x)), 0, width - 1);
            int xMax = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(p0.x, p1.x, p2.x)), 0, width - 1);
            int yMin = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(p0.y, p1.y, p2.y)), 0, height - 1);
            int yMax = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(p0.y, p1.y, p2.y)), 0, height - 1);

            float denom = (p1.y - p2.y) * (p0.x - p2.x) + (p2.x - p1.x) * (p0.y - p2.y);
            if (Mathf.Abs(denom) < 1e-7f) return;

            for (int y = yMin; y <= yMax; y++)
            {
                for (int x = xMin; x <= xMax; x++)
                {
                    float px = x + 0.5f;
                    float py = y + 0.5f;
                    float a = ((p1.y - p2.y) * (px - p2.x) + (p2.x - p1.x) * (py - p2.y)) / denom;
                    float b = ((p2.y - p0.y) * (px - p2.x) + (p0.x - p2.x) * (py - p2.y)) / denom;
                    float c = 1f - a - b;
                    if (a < 0f || b < 0f || c < 0f) continue;

                    // 重心座標で世界位置を求める
                    Vector3 worldPos = a * tri.v0WS + b * tri.v1WS + c * tri.v2WS;
                    Vector3 sp = camera.WorldToScreenPoint(worldPos);
                    if (sp.z < 0) continue;
                    float dx = sp.x - mouseScreenPx.x;
                    float dy = sp.y - mouseScreenPx.y;
                    if (dx * dx + dy * dy > radiusSqr) continue;

                    outPixels.Add(y * width + x);
                }
            }
        }

        /// <summary>
        /// UV(0-1)で表現された三角形をテクセルにラスタライズする。
        /// 重心座標で内部判定する単純実装（小三角形向き）。
        /// </summary>
        private static void RasterizeTriangle(Vector2 uv0, Vector2 uv1, Vector2 uv2, int width, int height, HashSet<int> outPixels)
        {
            // UV → テクセル座標
            Vector2 p0 = new Vector2(uv0.x * width, uv0.y * height);
            Vector2 p1 = new Vector2(uv1.x * width, uv1.y * height);
            Vector2 p2 = new Vector2(uv2.x * width, uv2.y * height);

            int xMin = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(p0.x, p1.x, p2.x)), 0, width - 1);
            int xMax = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(p0.x, p1.x, p2.x)), 0, width - 1);
            int yMin = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(p0.y, p1.y, p2.y)), 0, height - 1);
            int yMax = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(p0.y, p1.y, p2.y)), 0, height - 1);

            // 重心座標による内部判定
            float denom = (p1.y - p2.y) * (p0.x - p2.x) + (p2.x - p1.x) * (p0.y - p2.y);
            if (Mathf.Abs(denom) < 1e-7f) return;  // 退化三角形

            for (int y = yMin; y <= yMax; y++)
            {
                for (int x = xMin; x <= xMax; x++)
                {
                    float px = x + 0.5f;
                    float py = y + 0.5f;
                    float a = ((p1.y - p2.y) * (px - p2.x) + (p2.x - p1.x) * (py - p2.y)) / denom;
                    float b = ((p2.y - p0.y) * (px - p2.x) + (p0.x - p2.x) * (py - p2.y)) / denom;
                    float c = 1f - a - b;
                    if (a >= 0f && b >= 0f && c >= 0f)
                    {
                        outPixels.Add(y * width + x);
                    }
                }
            }
        }
    }
}
