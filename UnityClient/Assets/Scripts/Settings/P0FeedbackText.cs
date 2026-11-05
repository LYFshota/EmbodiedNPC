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

        // Only exact known adapter prefixes are normalized; never show upstream response bodies.
        public static (string Chinese, string English) ModelError(string code)
        {
            foreach (string prefix in new[] { "MODEL_DISCOVERY_", "OPENAI_COMPATIBLE_", "DEEPSEEK_", "OLLAMA_", "GEMINI_", "ANTHROPIC_" })
                if (code?.StartsWith(prefix, System.StringComparison.Ordinal) == true) { code = code.Substring(prefix.Length); break; }
            return code switch
            {
                "MODEL_SCHEMA_ENVELOPE_INVALID" or "MODEL_TEST_RESPONSE_INVALID" or "MODEL_CONTRACT_RESPONSE_INVALID" =>
                    ("模型已响应，但回复不符合要求的结构。请运行 NPC 兼容性测试或更换模型。", "The model responded, but its reply did not match the required structure. Run the NPC compatibility test or choose another model."),
                "TLS_FAILED" => ("与模型服务的安全连接中断，请稍后重试；持续失败时检查网络和 API 地址。", "The secure connection to the model service was interrupted. Retry shortly; if it persists, check the network and API URL."),
                "TLS_CERTIFICATE_FAILED" => ("模型服务的证书验证失败，请检查 API 地址、系统时间或联系服务商。", "The model service certificate could not be verified. Check the API URL and system clock, or contact the service."),
                "CONNECTION_FAILED" => ("无法建立模型服务连接，请检查网络和 API 地址后重试。", "Could not establish a connection to the model service. Check the network and API URL, then retry."),
                "BAD_REQUEST" => ("模型服务拒绝了请求，请检查 API 地址和供应商协议。", "The model service rejected the request. Check the API URL and provider protocol."),
                "TIMEOUT" => ("连接模型服务超时，请稍后重试或检查网络。", "The connection to the model service timed out. Retry shortly or check the network."),
                "MODEL_PROFILE_URL_INVALID" => ("API 地址无效，请填写服务商提供的完整地址。", "Invalid API URL. Enter the full address provided by your service."),
                "MODEL_PROFILE_MODEL_REQUIRED" => ("尚未填写模型名称，请使用服务商提供的模型 ID。", "A model name is required. Use the model ID supplied by your service."),
                "MODEL_NOT_FOUND" => ("服务中找不到此模型，请核对模型名称和访问权限。", "The service could not find this model. Check its name and your access."),
                "ENDPOINT_NOT_FOUND" => ("此服务没有模型列表接口，请手动填写模型 ID 后测试连接。", "This service has no model-list endpoint. Enter a model ID manually and test the connection."),
                "EMPTY" => ("服务未返回模型，请核对地址、权限和模型列表支持情况。", "The service returned no models. Check the URL, permissions and model-list support."),
                "MANUAL" => ("此服务需手动填写模型名称，请使用服务商提供的模型 ID。", "Enter the model name manually for this service, using its model ID."),
                "MODEL_PROFILE_DRAFT_INVALID" or "MODEL_CONFIG_VALUE_INVALID" => ("配置参数无效，请检查 AI 高级设置。", "Invalid configuration parameters. Check AI advanced settings."),
                "BACKEND_UNAVAILABLE" => ("无法连接本地 AI 服务，请检查启动窗口并刷新运行状态。", "Cannot reach the local AI service. Check the launcher and refresh runtime status."),
                "INVALID_RESPONSE" or "HTTP_ERROR" or "HTTP_FAILED" or "FAILED" =>
                    ("服务未返回有效结果，具体原因尚未确认。请核对 API 地址、模型及服务状态。", "No valid result was returned; the cause is unconfirmed. Check the API URL, model and service status."),
                _ => Dialogue(code)
            };
        }

        public static (string Chinese, string English) Dialogue(string code) => code switch
        {
            "OUTPUT_TRUNCATED" => ("回复达到输出长度上限，未能完成。请检查输出长度和思考设置，或运行 NPC 兼容性测试。", "The reply reached its output limit and could not finish. Check output and reasoning settings, or run the NPC compatibility test."),
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
            "THINKING_CONTROL_UNVERIFIED" =>
                ("模型返回了思考内容，当前接口未配置思考控制字段。请按服务商说明配置 AI 高级设置。", "The model returned reasoning, but no reasoning control field is configured. Check AI advanced settings against your service's instructions."),
            "THINKING_DISABLE_NOT_HONORED" =>
                ("已请求关闭思考，但模型服务仍返回思考内容。请向服务商确认关闭方式，或更换模型。", "Reasoning was requested off, but the service still returned reasoning. Ask the service how to disable it, or choose another model."),
            "TOTAL_GENERATION_BOUND_UNVERIFIED" or "OUTPUT_RESERVE_VIOLATED" =>
                ("无法确认模型遵守输出或思考上限。请核对 AI 高级设置与服务商支持的输出参数。", "The output or reasoning limit could not be verified. Check AI advanced settings against your service's output parameters."),
            "PARAMETER_REJECTED" or "REQUEST_REJECTED" =>
                ("模型服务拒绝了请求参数。请核对供应商、模型名称和 AI 高级设置。", "The service rejected request parameters. Check the provider, model name and AI advanced settings."),
            "SCHEMA_REJECTED" or "PROTOCOL_ERROR" =>
                ("模型接口或回复格式不符合要求。请核对所选供应商协议，或使用 NPC 兼容性测试排查。", "The model API or reply format did not meet requirements. Check the provider protocol or run the NPC compatibility test."),
            "COGNITIVE_INPUT_NOT_PERSISTED" => ("本次输入保存未确认，对话未能完成。", "Input storage was not confirmed. This dialogue could not complete."),
            "RESPONSE_JSON_INVALID" =>
                ("模型未返回可解析的单个回复，本次对话未能完成。请运行 NPC 兼容性测试，或更换模型。", "The model did not return a readable single result. Run the NPC compatibility test, or choose another model."),
            "RESPONSE_FIELDS_INVALID" =>
                ("模型回复缺少必要信息或字段格式不符。请运行 NPC 兼容性测试，或更换模型。", "The model reply is missing required information or has invalid fields. Run the NPC compatibility test, or choose another model."),
            "RESPONSE_TOO_LONG" =>
                ("模型回复超过允许长度，本次对话未能完成。请重试，或更换模型。", "The model reply exceeded the allowed length. Try again, or choose another model."),
            "RESPONSE_SCHEMA_INVALID" or "COGNITIVE_RESPONSE_INVALID" or "COGNITIVE_RESPONSE_MALFORMED" =>
                ("模型回复格式无效，本次对话未能完成。", "The model reply format was invalid. This dialogue could not complete."),
            "PRESENTATION_UNAVAILABLE" => ("回复已收到，但对话显示组件暂不可用。", "Reply received, but dialogue presentation is unavailable."),
            "DIALOGUE_PRESENTATION_HIDDEN" => ("对话界面已关闭，本次回复未展示。", "Dialogue was closed; this reply was not shown."),
            "DIALOGUE_SCOPE_CHANGED" or "DIALOGUE_SESSION_ENDED" =>
                ("原对话已结束或切换，本次回复未展示。", "The original dialogue ended or changed; this reply was not shown."),
            "WORLD_SPEAKER_UNAVAILABLE" or "WORLD_SPEECH_OWNER_CHANGED" or "WORLD_SPEECH_SCENE_CHANGED" =>
                ("原发言对象或场景已失效，本次回复未发出。", "The original speaker or world scene is no longer valid; this reply was not spoken."),
            "cancelled" => ("此请求已取消；保存状态另行确认。", "This request was cancelled; storage is confirmed separately."),
            _ => ("操作未能完成，原因尚未确认。请检查模型设置或查看诊断日志后重试。", "The operation could not complete; the cause is unconfirmed. Check model settings or diagnostic logs before retrying."),
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
