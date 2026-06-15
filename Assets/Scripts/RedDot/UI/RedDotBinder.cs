using UnityEngine;
using TMPro;
using Unity.VisualScripting;

namespace RedDot
{
    /// <summary>
    /// 挂载到需要显示红点的 UI GameObject，count > 0 时自动显示对应 UI。
    /// Number 类型显示数字角标，其余类型显示红点图标。
    /// </summary>
    [AddComponentMenu("RedDot/RedDot Binder")]
    public class RedDotBinder : MonoBehaviour
    {
        [SerializeField]
        [RedDotPathSelector]
        [Tooltip("从 RedDotPaths 常量中选择红点路径")]
        private int _redDotPathHash;

        [SerializeField]
        [Tooltip("超过此数量显示 \"99+\"")]
        private int _maxDisplayNumber = 99;

        private GameObject _newRedDotObject;
        private GameObject _normalRedDotObject;
        private GameObject _canUpdatRedDotObject;
        private GameObject _tipsRedDotObject;
        private GameObject _numRedDotObject;
        private TextMeshProUGUI _dotNumberText;

        private GameObject _dotObject
        {
            get
            {
                return _lastType switch
                {
                    RedDotType.IsNew => _newRedDotObject,
                    RedDotType.Normal => _normalRedDotObject,
                    RedDotType.CanUpdate => _canUpdatRedDotObject,
                    RedDotType.Tips => _tipsRedDotObject,
                    RedDotType.Number => _numRedDotObject,
                    _ => null,
                };
            }
        }

        private bool _lastVisible;
        private int _lastCount;
        private RedDotType _lastType;
        private bool _isBind;

        private void Awake()
        {
            _newRedDotObject = transform.Find("New")?.gameObject;
            _normalRedDotObject = transform.Find("Normal")?.gameObject;
            _canUpdatRedDotObject = transform.Find("CanUpgrade")?.gameObject;
            _tipsRedDotObject = transform.Find("Tips")?.gameObject;
            _numRedDotObject = transform.Find("Num")?.gameObject;
            _dotNumberText = transform.Find("Num/NumCount")?.GetComponent<TextMeshProUGUI>();

        }

        private void OnEnable()
        {
            ClearData();
            BindListener();
        }

        private void OnDisable()
        {
            UnBindListener();
        }

        private void OnRedDotChanged(RedDotState state)
        {
            Refresh(state);
        }

        private void Refresh(RedDotState state)
        {
            var visible = state.Visible;
            var count = state.TotalCount;
            var effectiveType = state.EffectiveType;

            if (visible == _lastVisible && count == _lastCount && effectiveType == _lastType)
                return;

            if (effectiveType != _lastType)
            {
                if (_dotObject != null && _dotObject.activeSelf)
                    _dotObject.SetActive(false);
            }

            _lastVisible = visible;
            _lastCount = count;
            _lastType = effectiveType;

            if (_dotObject != null && _dotObject.activeSelf != visible)
                _dotObject.SetActive(visible);

            if (_dotNumberText != null)
            {
                if (visible && effectiveType == RedDotType.Number)
                {
                    if (!_dotNumberText.gameObject.activeSelf)
                        _dotNumberText.gameObject.SetActive(true);

                    var display = count > _maxDisplayNumber ? $"{_maxDisplayNumber}+" : count.ToString();
                    if (_dotNumberText.text != display)
                        _dotNumberText.text = display;
                }
                else
                {
                    if (_dotNumberText.gameObject.activeSelf)
                        _dotNumberText.gameObject.SetActive(false);
                }
            }
        }

        [ContextMenu("Force Refresh")]
        public void ForceRefresh()
        {
            if (!RedDotManager.HasInstance)
                return;

            _lastVisible = !_lastVisible;
            _lastCount = -1;

            var state = RedDotManager.Instance.GetState(_redDotPathHash);
            Refresh(state);
        }

        public void SetPathHash(int newPathHash)
        {
            if (newPathHash == _redDotPathHash)
                return;

            UnBindListener();
            _redDotPathHash = newPathHash;
            ClearData();

            if (isActiveAndEnabled)
                BindListener();
        }

        private void ClearData()
        {
            _lastVisible = false;
            _lastCount = -1;
            _lastType = 0;
        }

        private void BindListener()
        {
            if (_isBind)
                return;

            RedDotManager.Instance.AddListener(_redDotPathHash, OnRedDotChanged);
            _isBind = true;
        }

        private void UnBindListener()
        {
            if (!_isBind)
                return;

            if (RedDotManager.HasInstance)
                RedDotManager.Instance.RemoveListener(_redDotPathHash, OnRedDotChanged);
            
            _isBind = false;
        }
    }
}
