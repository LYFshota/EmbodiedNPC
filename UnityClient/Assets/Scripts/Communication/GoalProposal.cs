using System;

namespace EmbodiedNPC.Communication
{
    /// <summary>
    /// 模型可提出、但尚未被 Unity 信任的 M4 高层目标。
    /// 支持当前 ActionCatalog 中声明的有界动作；参数和世界状态由 GoalValidator 权威验证。
    /// </summary>
    [Serializable]
    public sealed class GoalProposal
    {
        public string type;
        public string target_entity_id;
        public string destination_entity_id;
        public string drink_type;
        // Local trusted request context; never selectable by the model or accepted from JSON.
        [Newtonsoft.Json.JsonIgnore] internal string RequestingPlayerId;
    }
}
