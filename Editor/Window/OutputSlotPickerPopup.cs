using System;
using UnityEngine;
using UnityEditor;

namespace NekoareMaskTool.Editor
{
    /// <summary>
    /// 出力先のlilToonマスクスロットを選択するポップアップ
    /// </summary>
    public class OutputSlotPickerPopup : PopupWindowContent
    {
        private const float WINDOW_WIDTH = 220f;
        private const float ITEM_HEIGHT = 22f;
        private const float CATEGORY_HEIGHT = 20f;
        private const float PADDING = 4f;
        private const float INDENT_WIDTH = 12f;

        private readonly string _currentSlot;
        private readonly Action<string, string> _onSelect; // (propertyName, displayName)
        private Vector2 _scrollPos;

        private struct SlotDef
        {
            public string propertyName;
            public string displayName;
            public string category;
        }

        private static readonly SlotDef[] _slotDefs =
        {
            // メインカラー
            new SlotDef { category = "メインカラー", propertyName = "_Main2ndBlendMask", displayName = "2nd カラー マスク" },
            new SlotDef { category = "メインカラー", propertyName = "_Main3rdBlendMask", displayName = "3rd カラー マスク" },
            // 影
            new SlotDef { category = "影", propertyName = "_ShadowBorderMask", displayName = "影境界 マスク" },
            new SlotDef { category = "影", propertyName = "_ShadowBlurMask", displayName = "ぼかし量マスク" },
            // 発光
            new SlotDef { category = "発光", propertyName = "_EmissionBlendMask", displayName = "発光 マスク" },
            new SlotDef { category = "発光", propertyName = "_Emission2ndBlendMask", displayName = "発光2nd マスク" },
            // 光沢
            new SlotDef { category = "光沢", propertyName = "_SmoothnessTex", displayName = "滑らかさ" },
            new SlotDef { category = "光沢", propertyName = "_ReflectionColorTex", displayName = "反射 マスク" },
            // マットキャップ
            new SlotDef { category = "マットキャップ", propertyName = "_MatCapBlendMask", displayName = "マットキャップ マスク" },
            new SlotDef { category = "マットキャップ", propertyName = "_MatCap2ndBlendMask", displayName = "マットキャップ2nd マスク" },
            // 逆光ライト
            new SlotDef { category = "逆光ライト", propertyName = "_BacklightColorTex", displayName = "逆光ライト カラー" },
            // ラメ
            new SlotDef { category = "ラメ", propertyName = "_GlitterColorTex", displayName = "ラメ カラー" },
            // アルファマスク
            new SlotDef { category = "アルファマスク", propertyName = "_AlphaMask", displayName = "アルファマスク" },
            // アウトライン
            new SlotDef { category = "アウトライン", propertyName = "_OutlineWidthMask", displayName = "アウトライン幅 マスク" },
        };

        public OutputSlotPickerPopup(string currentSlot, Action<string, string> onSelect)
        {
            _currentSlot = currentSlot;
            _onSelect = onSelect;
        }

        public override Vector2 GetWindowSize()
        {
            // カテゴリ数を数える
            int categoryCount = 0;
            string lastCategory = null;
            foreach (var slot in _slotDefs)
            {
                if (slot.category != lastCategory)
                {
                    categoryCount++;
                    lastCategory = slot.category;
                }
            }

            // 「なし」の行 + セパレータ分を追加
            float height = ITEM_HEIGHT + PADDING + categoryCount * CATEGORY_HEIGHT + _slotDefs.Length * ITEM_HEIGHT + PADDING * 2;
            return new Vector2(WINDOW_WIDTH, Mathf.Min(height, 400f));
        }

        public override void OnGUI(Rect rect)
        {
            float contentHeight = CalculateContentHeight();

            _scrollPos = GUI.BeginScrollView(
                new Rect(0, 0, rect.width, rect.height),
                _scrollPos,
                new Rect(0, 0, rect.width - 16f, contentHeight)
            );

            float y = PADDING;

            // 「なし」オプション
            {
                var noneRect = new Rect(PADDING, y, rect.width - 16f - PADDING * 2, ITEM_HEIGHT);
                bool isNoneSelected = string.IsNullOrEmpty(_currentSlot);

                if (isNoneSelected)
                {
                    EditorGUI.DrawRect(noneRect, new Color(0.3f, 0.5f, 0.8f, 0.5f));
                }
                if (!isNoneSelected && noneRect.Contains(Event.current.mousePosition))
                {
                    EditorGUI.DrawRect(noneRect, new Color(0.3f, 0.5f, 0.8f, 0.2f));
                }

                var noneStyle = new GUIStyle(EditorStyles.label)
                {
                    fontStyle = isNoneSelected ? FontStyle.Bold : FontStyle.Normal
                };
                GUI.Label(noneRect, "なし", noneStyle);

                if (Event.current.type == EventType.MouseDown && noneRect.Contains(Event.current.mousePosition))
                {
                    _onSelect?.Invoke(null, null);
                    editorWindow?.Close();
                    Event.current.Use();
                }

                y += ITEM_HEIGHT;

                // セパレータ
                var separatorRect = new Rect(PADDING, y, rect.width - 16f - PADDING * 2, 1);
                EditorGUI.DrawRect(separatorRect, new Color(0.5f, 0.5f, 0.5f, 0.5f));
                y += PADDING;
            }

            string lastCategory = null;

            for (int i = 0; i < _slotDefs.Length; i++)
            {
                var slot = _slotDefs[i];

                // カテゴリヘッダー
                if (slot.category != lastCategory)
                {
                    lastCategory = slot.category;
                    var categoryRect = new Rect(PADDING, y, rect.width - 16f - PADDING * 2, CATEGORY_HEIGHT);
                    GUI.Label(categoryRect, slot.category, EditorStyles.boldLabel);
                    y += CATEGORY_HEIGHT;
                }

                // スロット行
                var itemRect = new Rect(INDENT_WIDTH, y, rect.width - 16f - INDENT_WIDTH - PADDING, ITEM_HEIGHT);
                bool isSelected = slot.propertyName == _currentSlot;

                // 選択中ハイライト
                if (isSelected)
                {
                    EditorGUI.DrawRect(itemRect, new Color(0.3f, 0.5f, 0.8f, 0.5f));
                }

                // ホバー
                if (!isSelected && itemRect.Contains(Event.current.mousePosition))
                {
                    EditorGUI.DrawRect(itemRect, new Color(0.3f, 0.5f, 0.8f, 0.2f));
                }

                var labelStyle = new GUIStyle(EditorStyles.label)
                {
                    fontStyle = isSelected ? FontStyle.Bold : FontStyle.Normal
                };
                GUI.Label(itemRect, slot.displayName, labelStyle);

                // クリック
                if (Event.current.type == EventType.MouseDown && itemRect.Contains(Event.current.mousePosition))
                {
                    _onSelect?.Invoke(slot.propertyName, slot.displayName);
                    editorWindow?.Close();
                    Event.current.Use();
                }

                y += ITEM_HEIGHT;
            }

            GUI.EndScrollView();
        }

        private float CalculateContentHeight()
        {
            float h = PADDING * 2;
            // 「なし」行 + セパレータ
            h += ITEM_HEIGHT + PADDING;
            string lastCategory = null;
            foreach (var slot in _slotDefs)
            {
                if (slot.category != lastCategory)
                {
                    h += CATEGORY_HEIGHT;
                    lastCategory = slot.category;
                }
                h += ITEM_HEIGHT;
            }
            return h;
        }
    }
}
