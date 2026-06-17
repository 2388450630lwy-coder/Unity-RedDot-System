using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace RedDot
{
    /// <summary>
    /// 红点系统测试面板 —— 覆盖背包、邮件、抽奖、属性点等场景。
    /// 挂到 Canvas 下，绑定对应 Button / Text 即可运行。
    /// </summary>
    public class TestPanel : MonoBehaviour
    {
        [Header("背包")]
        [SerializeField] private Button _btnAddBagItem;
        [SerializeField] private Button _btnReduceBagItem;

        [Header("属性点")]
        [SerializeField] private Button _btnAddAttribute;
        [SerializeField] private Button _btnReduceAttribute;

        [Header("免费抽奖")]
        [SerializeField] private Button _btnExistFreeLottery;
        [SerializeField] private Button _btnNotExistFreeLottery;

        [Header("广告商店")]
        [SerializeField] private Button _btnExistAdShop;
        [SerializeField] private Button _btnNotExistAdShop;


        [Header("系统邮件")]
        [SerializeField] private Button _btnAddSystemMail;
        [SerializeField] private Button _btnReduceSystemMail;

        [Header("个人邮件")]
        [SerializeField] private Button _btnAddPersonMail;
        [SerializeField] private Button _btnReducePersonMail;

        // 本地计数器
        private int _bagCount;
        private int _attrCount;
        private bool _hasFreeLottery;
        private bool _hasAdShop;
        private int _systemMailCount;
        private int _personMailCount;

        private void Awake()
        {
            BindButtons();
            RefreshAllUI();
        }

        // ═══ 按钮 ═══

        private void BindButtons()
        {
            if (_btnAddBagItem) _btnAddBagItem.onClick.AddListener(OnAddBagItemCountHandler);
            if (_btnReduceBagItem) _btnReduceBagItem.onClick.AddListener(OnReduceBagItemCountHandler);
            if (_btnAddAttribute) _btnAddAttribute.onClick.AddListener(OnAddAttributeCountHandler);
            if (_btnReduceAttribute) _btnReduceAttribute.onClick.AddListener(OnReduceAttributeCountHandler);
            if (_btnExistFreeLottery) _btnExistFreeLottery.onClick.AddListener(OnExistFreeLotteryCountHandler);
            if (_btnNotExistFreeLottery) _btnNotExistFreeLottery.onClick.AddListener(OnNotExistFreeLotteryCountHandler);
            if (_btnExistAdShop) _btnExistAdShop.onClick.AddListener(OnExistAdShopItemCountHandler);
            if (_btnNotExistAdShop) _btnNotExistAdShop.onClick.AddListener(OnNotExistAdShopItemCountHandler);
            if (_btnAddSystemMail) _btnAddSystemMail.onClick.AddListener(OnAddSystemMailCountHandler);
            if (_btnReduceSystemMail) _btnReduceSystemMail.onClick.AddListener(OnReduceSystemMailCountHandler);
            if (_btnAddPersonMail) _btnAddPersonMail.onClick.AddListener(OnAddPersonMailCountHandler);
            if (_btnReducePersonMail) _btnReducePersonMail.onClick.AddListener(OnReducePersonMailCountHandler);
        }

        // ═══ 背包 ═══

        public void OnAddBagItemCountHandler()
        {
            _bagCount++;
            RedDotManager.Instance.SetRedDot(RedDotPaths.Root_Bag_ItemCount, _bagCount, RedDotType.Number);
            RefreshBagUI();
        }

        public void OnReduceBagItemCountHandler()
        {
            _bagCount = Mathf.Max(0, _bagCount - 1);
            RedDotManager.Instance.SetRedDot(RedDotPaths.Root_Bag_ItemCount, _bagCount, RedDotType.Number);
            RefreshBagUI();
        }

        // ═══ 属性点 ═══

        public void OnAddAttributeCountHandler()
        {
            _attrCount++;
            RedDotManager.Instance.SetRedDot(RedDotPaths.Root_Role_AttrPoint, _attrCount, RedDotType.CanUpdate);
            RefreshAttributeUI();
        }

        public void OnReduceAttributeCountHandler()
        {
            _attrCount = Mathf.Max(0, _attrCount - 1);
            RedDotManager.Instance.SetRedDot(RedDotPaths.Root_Role_AttrPoint, _attrCount, RedDotType.CanUpdate);
            RefreshAttributeUI();
        }

        // ═══ 免费抽奖 ═══

        public void OnExistFreeLotteryCountHandler()
        {
            _hasFreeLottery = true;
            RedDotManager.Instance.SetRedDot(RedDotPaths.Root_Shop_Lottery_Free, 1, RedDotType.Tips);
            RefreshFreeLotteryUI();
        }

        public void OnNotExistFreeLotteryCountHandler()
        {
            _hasFreeLottery = false;
            RedDotManager.Instance.ClearNode(RedDotPaths.Root_Shop_Lottery_Free);
            RefreshFreeLotteryUI();
        }

        // ═══ 广告商店 ═══

        public void OnExistAdShopItemCountHandler()
        {
            _hasAdShop = true;
            RedDotManager.Instance.SetRedDot(RedDotPaths.Root_Shop_Lottery_Adv, 1, RedDotType.Tips);
            RefreshAdShopUI();
        }

        public void OnNotExistAdShopItemCountHandler()
        {
            _hasAdShop = false;
            RedDotManager.Instance.ClearNode(RedDotPaths.Root_Shop_Lottery_Adv);
            RefreshAdShopUI();
        }

        // ═══ 系统邮件 ═══

        public void OnAddSystemMailCountHandler()
        {
            _systemMailCount++;
            RedDotManager.Instance.SetRedDot(RedDotPaths.Root_Mail_System, _systemMailCount, RedDotType.IsNew);
            RefreshSystemMailUI();
        }

        public void OnReduceSystemMailCountHandler()
        {
            _systemMailCount = Mathf.Max(0, _systemMailCount - 1);
            RedDotManager.Instance.SetRedDot(RedDotPaths.Root_Mail_System, _systemMailCount, RedDotType.IsNew);
            RefreshSystemMailUI();
        }

        // ═══ 个人邮件 ═══

        public void OnAddPersonMailCountHandler()
        {
            _personMailCount++;
            RedDotManager.Instance.SetRedDot(RedDotPaths.Root_Mail_Person, _personMailCount, RedDotType.IsNew);
            RefreshPersonMailUI();
        }

        public void OnReducePersonMailCountHandler()
        {
            _personMailCount = Mathf.Max(0, _personMailCount - 1);
            RedDotManager.Instance.SetRedDot(RedDotPaths.Root_Mail_Person, _personMailCount, RedDotType.IsNew);
            RefreshPersonMailUI();
        }

        // ═══ UI = ═══

        private void RefreshAllUI()
        {
            RefreshBagUI();
            RefreshAttributeUI();
            RefreshFreeLotteryUI();
            RefreshAdShopUI();
            RefreshSystemMailUI();
            RefreshPersonMailUI();
        }

        private void RefreshBagUI()
        {
            Debug.Log($"[红点测试] 背包物品数: {_bagCount}  |  自身红点: {RedDotManager.Instance.GetSelfRedDot(RedDotPaths.Root_Bag_ItemCount)}  |  背包红点: {RedDotManager.Instance.GetRedDot(RedDotPaths.Root_Bag)}");
        }

        private void RefreshAttributeUI()
        {
            Debug.Log($"[红点测试] 属性点: {_attrCount}  |  自身红点: {RedDotManager.Instance.GetSelfRedDot(RedDotPaths.Root_Role_AttrPoint)}  |  角色红点: {RedDotManager.Instance.GetRedDot(RedDotPaths.Root_Role)}");
        }

        private void RefreshFreeLotteryUI()
        {
            Debug.Log($"[红点测试] 免费抽奖: {(_hasFreeLottery ? "有" : "无")}  |  抽奖红点: {RedDotManager.Instance.GetRedDot(RedDotPaths.Root_Shop_Lottery)}");
        }

        private void RefreshAdShopUI()
        {
            Debug.Log($"[红点测试] 广告商店: {(_hasAdShop ? "有" : "无")}  |  抽奖红点: {RedDotManager.Instance.GetRedDot(RedDotPaths.Root_Shop_Lottery)}");
        }

        private void RefreshSystemMailUI()
        {
            Debug.Log($"[红点测试] 系统邮件: {_systemMailCount}  |  邮箱红点: {RedDotManager.Instance.GetRedDot(RedDotPaths.Root_Mail)}");
        }

        private void RefreshPersonMailUI()
        {
            Debug.Log($"[红点测试] 个人邮件: {_personMailCount}  |  邮箱红点: {RedDotManager.Instance.GetRedDot(RedDotPaths.Root_Mail)}");
        }
    }
}
