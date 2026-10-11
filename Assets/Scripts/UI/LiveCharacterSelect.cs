using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Live 选人界面的角色槽位卡片组件
/// 负责呈现选中角色的头像、服装图标与槽位编号，并处理槽位点击选择交互。
/// </summary>
public class LiveCharacterSelect : MonoBehaviour
{
    /// <summary>当前选中的角色信息数据</summary>
    public CharaEntry CharaEntry;
    /// <summary>选中的身体服装 ID</summary>
    public string CostumeId;
    /// <summary>选中的头部服装 ID</summary>
    public string HeadCostumeId;
    /// <summary>角色头像 Image 组件</summary>
    public Image CharaImage;
    /// <summary>角色服装图标 Image 组件</summary>
    public Image CostumeImage;
    /// <summary>槽位序号文本（1, 2, 3...）</summary>
    public Text IndexText;

    /// <summary>
    /// 点击当前槽位触发选人操作
    /// </summary>
    /// <param name="ui">UmaViewerUI 实例</param>
    public void SelectChara(UmaViewerUI ui) 
    {
        if (!ui) return;
        if (ui.CurrentLive == null) return;

        ui.CurrentSeletChara = this;
        // 进入 Live 选人模式，激活音轨小图标与筛选按钮
        LiveVocalSelectManager.Instance.EnterLiveSelectMode(ui.CurrentLive.MusicId, this);

        if (!ui.SelectCharacterPannel.activeInHierarchy)
            ui.ToggleUIPanel(ui.SelectCharacterPannel);
    }

    /// <summary>
    /// 设置并刷新当前槽位展示的角色与服装信息
    /// </summary>
    /// <param name="charaentry">角色数据</param>
    /// <param name="costumeId">服装 ID</param>
    /// <param name="costumeSprite">服装图标</param>
    /// <param name="headCostumeId">头部装扮 ID</param>
    public void SetValue(CharaEntry charaentry, string costumeId, Sprite costumeSprite, string headCostumeId)
    {
        CharaEntry = charaentry;
        CostumeId = costumeId;
        HeadCostumeId = headCostumeId;
        CharaImage.enabled = true;
        CharaImage.sprite = charaentry.Icon;
        CostumeImage.sprite = costumeSprite;
    }
}

