namespace EmbodiedNPC.Settings
{
    // Presentation only. Never infer a provider failure from an untrusted response body.
    public static class P0FeedbackText
    {
        public static (string Chinese, string English) BackendError(string code, long httpStatus = 0)
        {
            if (code == "BACKEND_UNAVAILABLE" && httpStatus == 0)
                return ("无法连接 AI 服务，请确认 Backend 已启动并重试。", "Cannot reach the AI service. Check that Backend is running and retry.");
            if (code == "TIMEOUT" && httpStatus == 0)
                return ("连接检查超时，请稍后重试。", "Connection check timed out. Try again shortly.");
            if (httpStatus == 404 || code == "UNSUPPORTED_MODEL_CONFIG")
                return ("模型配置接口不兼容，请检查客户端与 Backend 版本。", "Model settings API is incompatible. Check client and Backend versions.");
            return ("AI 服务状态无法读取，请检查配置或版本后重试。", "AI service status could not be read. Check settings or versions and retry.");
        }

        public static (string Chinese, string English) Dialogue(string code) => code switch
        {
            "WAITING" => ("正在等待回复。", "Waiting for a reply."),
            "ACK_UNKNOWN" => ("本次记录保存未确认，请检查连接；不要重复提交。", "Record storage is unconfirmed. Check the connection; do not resubmit."),
            "CLIENT_TIMEOUT" => ("等待回复超时，服务端可能仍在处理。", "Waiting for a reply timed out. The server may still be processing it."),
            "SEND_FAILED" => ("对话发送未确认，请检查连接后再操作。", "Dialogue delivery is unconfirmed. Check the connection before proceeding."),
            "AUTH_FAILED" => ("模型密钥无效或无访问权限，请检查模型设置。", "The model key is invalid or lacks access. Check model settings."),
            "TIMEOUT" => ("模型响应超时，请稍后重试。", "The model response timed out. Try again later."),
            "UNAVAILABLE" => ("无法连接模型服务，请检查网络与模型设置。", "Cannot reach the model service. Check the network and model settings."),
            "RATE_LIMITED" => ("模型服务请求受限，请稍后重试。", "Model requests are limited. Try again later."),
            "CONTEXT_BUDGET_EXCEEDED" or "COGNITIVE_PROMPT_BUDGET_EXCEEDED" or "CONTEXT_LENGTH_EXCEEDED" => EmbodiedNPC.Cognition.ContextCapacityFeedback.ErrorMessage,
            "OUTPUT_RESERVE_EXCEEDS_CONTEXT" => ("输出或思考预留超过上下文容量。请降低输出上限、关闭思考或选择模型支持的更大档位。", "Output or thinking reserve exceeded context capacity. Lower the output limit, disable thinking, or choose a larger supported context preset."),
            "COGNITIVE_INPUT_NOT_PERSISTED" => ("本次输入保存未确认，对话未能完成。", "Input storage was not confirmed. This dialogue could not complete."),
            "COGNITIVE_RESPONSE_INVALID" or "COGNITIVE_RESPONSE_MALFORMED" =>
                ("模型回复格式无效，本次对话未能完成。", "The model reply format was invalid. This dialogue could not complete."),
            "cancelled" => ("此请求已取消；保存状态另行确认。", "This request was cancelled; storage is confirmed separately."),
            _ => ("本次对话未能完成，请检查模型设置或稍后重试。", "This dialogue could not complete. Check model settings or try again later."),
        };

        public static (string Chinese, string English) IndexError(string code) => code switch
        {
            "EMBEDDING_CONTROL_STATE_INVALID" => ("控制状态无法读取，不能恢复；请备份后人工检查。", "Control state cannot be read; recovery is blocked. Back up and review it manually."),
            "EMBEDDING_CONTROL_WRITE_FAILED" => ("控制状态保存失败，请检查文件权限后重新检查。", "Control state could not be saved. Check file permissions and check again."),
            "EMBEDDING_INDEX_FAILED" => ("索引因处理错误暂停；检查存储后手动恢复。", "Indexing paused due to a processing error. Check storage and resume manually."),
            "EMBEDDING_PROFILE_NOT_APPLIED" or "EMBEDDING_MANAGED_PROFILE_REQUIRED" =>
                ("配置尚未生效，请应用 Embedding 并重启 Backend。", "Settings are not active. Apply embedding and restart Backend."),
            "EMBEDDING_CLOUD_CONFIRMATION_REQUIRED" => ("恢复前需确认云端摘要发送及可能费用。", "Confirm cloud summary transfer and possible charges before resuming."),
            "EMBEDDING_REQUEST_FAILED" or "EMBEDDING_PREVIOUS_REQUEST_FAILED" =>
                ("向量请求已停止；检查模型配置后手动恢复。", "Vector requests stopped. Check model settings and resume manually."),
            _ => ("索引状态或操作未确认，请重新检查后再操作。", "Index status or operation is unconfirmed. Check again before proceeding."),
        };
    }
}
