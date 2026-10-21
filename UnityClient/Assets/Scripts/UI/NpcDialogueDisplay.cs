using System.Collections;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Planning;
using TMPro;
using UnityEngine;

namespace EmbodiedNPC.UI
{
    /// <summary>
    /// Displays cognitive utterances and deterministic plan status.
    /// </summary>
    public class NpcDialogueDisplay : MonoBehaviour, ICognitiveResponsePresenter
    {
        [SerializeField] private GoalPlanRunner goalPlanRunner;

        [Header("字幕文本")]
        [SerializeField]
        [Tooltip("Canvas 中用于显示 NPC 台词的 TextMeshPro 文本组件。")]
        private TMP_Text dialogueText;

        [SerializeField]
        [Min(0f)]
        [Tooltip("台词显示多久；0 表示一直显示最新一句。")]
        private float visibleSeconds = 5f;

        private Coroutine hideCoroutine;
        public string DisplayedText => dialogueText == null ? string.Empty : dialogueText.text;
        public bool IsVisible => dialogueText != null && dialogueText.gameObject.activeSelf;

        private void Awake()
        {
            if (goalPlanRunner == null) goalPlanRunner = FindAnyObjectByType<GoalPlanRunner>();
        }

        private void OnEnable()
        {
            if (goalPlanRunner != null) goalPlanRunner.PresentationReady += HandleGoalPresentation;
        }

        private void OnDisable()
        {
            if (goalPlanRunner != null) goalPlanRunner.PresentationReady -= HandleGoalPresentation;
        }

        private void Start()
        {
            SetVisible(false);
        }

        private void HandleGoalPresentation(GoalPlanPresentation presentation)
        {
            string text = presentation.Kind switch
            {
                GoalPlanPresentationKind.Rejected => ToRejectedText(presentation.Code),
                GoalPlanPresentationKind.Failed => ToFailedText(presentation.Code, presentation.Message),
                _ when !string.IsNullOrWhiteSpace(presentation.Message) => presentation.Message,
                GoalPlanPresentationKind.Accepted => "我正在确认是否能够执行这个请求。",
                GoalPlanPresentationKind.Recovering => "刚才没有成功，我再试一次。",
                GoalPlanPresentationKind.Succeeded => "任务已经完成。",
                GoalPlanPresentationKind.Clarification => "我需要你再说明一下。",
                _ => "这个请求没有成功完成。",
            };
            Show(text);
        }

        private static string ToRejectedText(string code) => code switch
        {
            "TargetAlreadyHeld" => "这个物品已经在你手里，无法再次交付。",
            "TargetNotFound" => "这里没有这个可交付物品。",
            "TargetUnreachable" => "我现在无法到达这个物品。",
            "HandOccupied" => "我现在正拿着别的物品。",
            "TARGET_LOCATION_UNKNOWN" => "我现在没看见它，也没有可靠的位置记忆。请让我再看一下目标。",
            "REDISCOVERY_ACTOR_UNAVAILABLE" => "我现在暂时无法前往之前的位置。",
            _ => "这个请求现在无法执行。",
        };

        private static string ToFailedText(string code, string fallbackMessage)
        {
            if (code?.StartsWith("CLARIFICATION_REQUIRED:", System.StringComparison.Ordinal) == true)
            {
                return "我还缺少完成请求所需的信息，请再说明具体目标。";
            }

            return code switch
            {
                "INTERRUPTED_BY_THREAT" => "有危险，我需要先确保安全。",
                "TARGET_NOT_REDISCOVERED" => "我记得它之前在这里，但现在没有找到。能告诉我新的位置吗？",
                "REDISCOVERY_PATH_UNAVAILABLE" => "我记得它之前的位置，但现在走不到那里。",
                "REDISCOVERY_TIMEOUT" => "我还没能完成这次查找，先停下来；之前的位置记忆仍保留。",
                "TARGET_NOT_CURRENTLY_VISIBLE" => "我又失去了目标的视线，先停下来。请让我重新看见它。",
                "SAME_FAILURE_RETRY_EXHAUSTED" or "GOAL_RECOVERY_BUDGET_EXHAUSTED" =>
                    "我已经尝试过，但这个请求现在仍无法完成。",
                _ => !string.IsNullOrWhiteSpace(fallbackMessage)
                    ? fallbackMessage
                    : "这个请求没有成功完成。",
            };
        }

        public bool TryPresentCognitiveUtterance(string utterance)
        {
            if (string.IsNullOrWhiteSpace(utterance) || dialogueText == null) return false;
            Show(utterance);
            return true;
        }

        private void Show(string text)
        {
            if (dialogueText == null)
            {
                Debug.LogWarning("[NpcDialogueDisplay] 未配置 Dialogue Text，无法显示 NPC 台词。");
                return;
            }

            dialogueText.text = text;
            SetVisible(true);
            Debug.Log($"[NpcDialogueDisplay] 显示 NPC 台词：{text}");

            if (hideCoroutine != null)
            {
                StopCoroutine(hideCoroutine);
            }

            if (visibleSeconds > 0f)
            {
                hideCoroutine = StartCoroutine(HideAfterDelay());
            }
        }

        private IEnumerator HideAfterDelay()
        {
            yield return new WaitForSeconds(visibleSeconds);
            SetVisible(false);
            hideCoroutine = null;
        }

        private void SetVisible(bool isVisible)
        {
            if (dialogueText != null)
            {
                dialogueText.gameObject.SetActive(isVisible);
            }
        }
    }
}
