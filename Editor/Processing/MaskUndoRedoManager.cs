using UnityEngine;

namespace NekoareMaskTool.Editor
{
    /// <summary>
    /// マスクのUndo/Redo管理クラス
    /// テクスチャサイズに応じて動的に履歴数を調整
    /// </summary>
    public class MaskUndoRedoManager
    {
        private const int BASE_MEMORY_BUDGET = 20 * 1024 * 1024; // 20MB
        private const int MIN_HISTORY = 10;
        private const int MAX_HISTORY = 50;

        private float[][] _historyStates;
        private int _currentIndex = -1;
        private int _historyCount = 0;
        private int _maxHistory;

        public bool CanUndo => _currentIndex > 0;
        public bool CanRedo => _currentIndex < _historyCount - 1;

        public void Initialize(int width, int height)
        {
            // テクスチャサイズに基づいて最大履歴数を計算
            int pixelCount = width * height;
            int bytesPerState = pixelCount * sizeof(float);
            int calculatedMax = BASE_MEMORY_BUDGET / bytesPerState;

            _maxHistory = Mathf.Clamp(calculatedMax, MIN_HISTORY, MAX_HISTORY);

            _historyStates = new float[_maxHistory][];
            _currentIndex = -1;
            _historyCount = 0;

            // Debug.Log($"MaskUndoRedoManager initialized: {width}x{height}, MaxHistory={_maxHistory}");
        }

        public void RecordState(float[] state)
        {
            if (state == null || _historyStates == null)
                return;

            // 現在の位置より後ろの履歴を削除（Redo履歴をクリア）
            if (_currentIndex < _historyCount - 1)
            {
                for (int i = _currentIndex + 1; i < _historyCount; i++)
                {
                    _historyStates[i] = null;
                }
                _historyCount = _currentIndex + 1;
            }

            // 履歴がいっぱいの場合、最古の履歴を削除
            if (_historyCount >= _maxHistory)
            {
                // 配列を1つ前にシフト
                for (int i = 1; i < _maxHistory; i++)
                {
                    _historyStates[i - 1] = _historyStates[i];
                }
                _historyCount = _maxHistory - 1;
                _currentIndex = _historyCount - 1;
            }

            // 新しい状態を記録
            _currentIndex++;
            _historyCount = _currentIndex + 1;

            _historyStates[_currentIndex] = new float[state.Length];
            System.Array.Copy(state, _historyStates[_currentIndex], state.Length);
        }

        public float[] Undo()
        {
            if (!CanUndo)
                return null;

            _currentIndex--;
            return GetCurrentState();
        }

        public float[] Redo()
        {
            if (!CanRedo)
                return null;

            _currentIndex++;
            return GetCurrentState();
        }

        private float[] GetCurrentState()
        {
            if (_currentIndex < 0 || _currentIndex >= _historyCount)
                return null;

            return _historyStates[_currentIndex];
        }

        public void Clear()
        {
            for (int i = 0; i < _historyStates.Length; i++)
            {
                _historyStates[i] = null;
            }
            _currentIndex = -1;
            _historyCount = 0;
        }

        public int GetHistoryCount()
        {
            return _historyCount;
        }

        public int GetMaxHistory()
        {
            return _maxHistory;
        }
    }
}
