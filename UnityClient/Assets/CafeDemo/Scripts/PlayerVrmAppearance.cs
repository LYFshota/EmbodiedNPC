using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EmbodiedNPC.Animation;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Settings;
using UniGLTF;
using UniVRM10;
using UnityEngine;

namespace CafeDemo
{
    /// <summary>One local player's visual transaction. Actor, input, identity, collision and camera are never replaced.</summary>
    [DisallowMultipleComponent]
    public sealed class PlayerVrmAppearance : MonoBehaviour
    {
        private Transform visual;
        private GameObject defaultModel, importedModel;
        private Animator defaultAnimator, currentAnimator;
        private HumanoidMotionAdapter adapter;
        private CafeDemoPlayerController movement;
        private PlayerEmoteController emotes;
        private MotionChannelCoordinator motion;
        private DialogueController dialogue;
        private CafeDemoOrbitCamera orbit;
        private PlayerAvatarStore store;
        private string activeAvatarReference;
        public string AppearanceDescription => activeAvatarReference == store?.Selected ? store?.AppearanceDescription : null;
        public bool SaveAppearanceDescription(string value)
        {
            if (IsLoading || store == null || activeAvatarReference != store.Selected || !store.SetAppearanceDescription(value)) return false;
            SetDiagnostic(null); return true;
        }
        private readonly CancellationTokenSource lifetime = new();
        private float targetHeight, groundOffset;
        private bool initialized;
        public bool RestorationComplete { get; private set; }
        public bool IsLoading { get; private set; }
        public bool IsCustom => importedModel != null;
        public Animator CurrentAnimator => currentAnimator;
        public string Diagnostic { get; private set; }
        public string CurrentName { get; private set; } = "Xingmang";
        public PlayerAvatarStore Store => store;
        public event Action Changed;

        public void Initialize(Animator original, PlayerAvatarStore isolatedStore = null)
        {
            if (initialized) return;
            initialized = true; store = isolatedStore ?? PlayerAvatarStore.OpenDefault();
            movement = GetComponent<CafeDemoPlayerController>(); emotes = GetComponent<PlayerEmoteController>();
            motion = GetComponent<MotionChannelCoordinator>(); dialogue = FindAnyObjectByType<DialogueController>(); orbit = FindAnyObjectByType<CafeDemoOrbitCamera>();
            if (original == null || original.transform == transform || !HumanoidMotionAdapter.ValidateHumanoid(original, out _))
            { SetDiagnostic("Default player Humanoid is unavailable."); return; }
            visual = original.transform;
            while (visual.parent != null && visual.parent != transform) visual = visual.parent;
            if (visual.parent != transform || visual == original.transform)
            { SetDiagnostic("Player requires a stable Visual wrapper."); visual = null; return; }
            Transform model = original.transform;
            while (model.parent != visual) model = model.parent;
            defaultModel = model.gameObject; defaultAnimator = currentAnimator = original;
            var renderers = defaultModel.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) { SetDiagnostic("Default visual has no renderer."); visual = null; return; }
            Bounds bounds = renderers[0].bounds; foreach (var r in renderers) bounds.Encapsulate(r.bounds);
            targetHeight = bounds.size.y / visual.lossyScale.y;
            groundOffset = visual.InverseTransformPoint(new Vector3(bounds.center.x, bounds.min.y, bounds.center.z)).y;
            if (!float.IsFinite(targetHeight) || targetHeight < .5f || targetHeight > 3f)
            { SetDiagnostic("Default visual scale is unsupported."); visual = null; return; }
            adapter = GetComponent<HumanoidMotionAdapter>() ?? gameObject.AddComponent<HumanoidMotionAdapter>();
            adapter.Configure(visual, original);
            SetDiagnostic(null);
        }
        private async void Start()
        {
            if (!initialized) { RestorationComplete = true; return; } // Explicitly initialized by the existing player composition.
            try
            {
                await Task.Yield();
                if (this == null || lifetime.IsCancellationRequested || visual == null || string.IsNullOrEmpty(store.Selected)) return;
                string saved = store.Selected;
                bool loaded = await ImportPathAsync(store.ResolveSelection(saved), false);
                if (!loaded) store.Select(null); // Keep default visible; a broken choice never blocks startup.
            }
            catch (Exception failure)
            {
                if (this != null && !lifetime.IsCancellationRequested)
                { store.Select(null); SetDiagnostic("Saved avatar could not be restored; using the default avatar. " + SafeReason(failure)); }
            }
            finally { RestorationComplete = true; }
        }
        public bool IsBusy => BusyReason() != null;
        private string BusyReason()
        {
            if (emotes != null && emotes.IsActive) return "emote";
            if (motion != null)
            {
                if (motion.IsTaskMotionActive) return "task_motion";
                if (motion.IsEmergencyMotionActive) return "emergency_motion";
                if (motion.IsCarryPresentationActive) return "carry";
                if (motion.ActiveGestureId != 0) return "gesture";
            }
            if (movement != null)
            {
                if (movement.IsPresentationInputSuppressed) return "presentation_camera";
                if (movement.Controller != null && Vector3.ProjectOnPlane(movement.Controller.velocity, Vector3.up).sqrMagnitude > .01f)
                    return "movement";
            }
            // Closing input retains the accepted conversation. Only live player interaction blocks a visual swap.
            if (dialogue != null && (dialogue.IsPresentationVisible || dialogue.IsChangingSession)) return "interaction";
            return null;
        }
        private static string BlockMessage(string reason) => reason switch
        {
            "loading" => SettingsUiLanguage.T("正在导入角色，请稍候。", "An avatar import is already in progress. Please wait."),
            "not_ready" => SettingsUiLanguage.T("玩家外观尚未准备好，请重新进入场景后重试。", "Player appearance is not ready. Re-enter the scene and retry."),
            "movement" => SettingsUiLanguage.T("请先停止移动，再切换角色。", "Stop moving before changing appearance."),
            "emote" or "gesture" => SettingsUiLanguage.T("请等待挥手或表情动作结束，再切换角色。", "Wait for the emote or gesture to finish before changing appearance."),
            "presentation_camera" => SettingsUiLanguage.T("请先退出自由展示相机，再切换角色。", "Exit the presentation camera before changing appearance."),
            "interaction" => SettingsUiLanguage.T("请先关闭当前对话，再切换角色。", "Close the current dialogue before changing appearance."),
            _ => SettingsUiLanguage.T("请先结束当前动作，再切换角色。", "Wait for the current action to finish before changing appearance.")
        };
        private bool AdmitChange()
        {
            string reason = IsLoading ? "loading" : visual == null || adapter == null ? "not_ready" : BusyReason();
            if (reason == null) return true;
            Debug.Log($"[PlayerVRM] stage=admission result=rejected reason={reason} " +
                $"session={dialogue != null && dialogue.HasActiveSession} dialogueVisible={dialogue != null && dialogue.IsPresentationVisible}", this);
            SetDiagnostic(BlockMessage(reason)); return false;
        }

        public Task<bool> ImportScannedAsync(string filename)
        {
            try { return ImportPathAsync(store.ScannedPath(filename), true); }
            catch (Exception failure) { SetDiagnostic(SafeReason(failure)); return Task.FromResult(false); }
        }
        public async Task<bool> ImportPathAsync(string path, bool persist = true)
        {
            if (!AdmitChange()) return false;
            IsLoading = true; SetDiagnostic(null);
            string stage = "read";
            void Stage(string value) { stage = value; Debug.Log("[PlayerVRM] stage=" + value, this); }
            GameObject candidate = null; bool bound = false;
            Vrm10FastSpringboneRuntimeStandalone pendingSpring = null;
            Animator old = currentAnimator;
            var oldState = new AnimatorSnapshot(old);
            try
            {
                Stage("read");
                byte[] bytes = await Task.Run(() => PlayerAvatarStore.ReadBounded(path), lifetime.Token);
                lifetime.Token.ThrowIfCancellationRequested();
                // UniGLTF owns parsing. Empty source path + URI rejection prevent external resource resolution.
                Stage("parse");
                using (var data = GlbLowLevelParser.Parse("", bytes)) PlayerVrmBudget.Validate(data);
                // Use UniVRM's per-model spring runtime. Retired imports never enter the shared service's deferred buffer removal.
                // LoadBytesAsync exposes this option in the pinned version; LoadGltfDataAsync does not.
                pendingSpring = new Vrm10FastSpringboneRuntimeStandalone();
                Stage("load");
                var instance = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: true,
                    controlRigGenerationOption: ControlRigGenerationOption.None, showMeshes: false, ct: lifetime.Token,
                    springboneRuntime: pendingSpring);
                if (instance == null) throw new InvalidDataException("UniVRM could not load this VRM version.");
                candidate = instance.gameObject; pendingSpring = null; // Vrm10Instance now owns disposal.
                lifetime.Token.ThrowIfCancellationRequested();
                if (this == null || visual == null) throw new OperationCanceledException();
                Stage("validate_instance");
                var resources = candidate.GetComponent<RuntimeGltfInstance>();
                var bounds = PlayerVrmBudget.ValidateLoaded(candidate, resources);
                var animator = candidate.GetComponent<Animator>();
                if (!HumanoidMotionAdapter.ValidateHumanoid(animator, out string rigError)) throw new InvalidDataException(rigError);
                float scale = targetHeight / bounds.size.y;
                candidate.name = "ImportedPlayerVisual";
                candidate.transform.SetParent(visual, false);
                // UniVRM migrates VRM 0.x into VRM 1.0's canonical forward convention.
                candidate.transform.localRotation = Quaternion.identity;
                candidate.transform.localScale = Vector3.one * scale;
                candidate.transform.localPosition = Vector3.up * (groundOffset - bounds.min.y * scale);
                foreach (Transform child in candidate.GetComponentsInChildren<Transform>(true)) child.gameObject.layer = defaultModel.layer;
                animator.runtimeAnimatorController = defaultAnimator.runtimeAnimatorController;
                animator.applyRootMotion = false; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.Rebind(); animator.Update(0);
                await Task.Yield();
                lifetime.Token.ThrowIfCancellationRequested();
                Stage("commit_guard");
                string busy = BusyReason();
                if (busy != null) throw new InvalidOperationException(BlockMessage(busy));
                Stage("preserve");
                string reference = persist ? store.Preserve(bytes) : PlayerAvatarStore.ContentReference(bytes);
                // No await between binding, persistence, visibility and retirement: the commit is one main-thread step.
                bound = true; // Roll back even if an unexpected consumer exception occurs part way through TryBind.
                Stage("bind");
                if (!adapter.TryBind(animator, out string error)) { bound = false; throw new InvalidDataException(error); }
                resources.ShowMeshes();
                Stage("save");
                if (persist && !store.Select(reference)) throw new IOException("Could not save avatar settings. Original appearance retained.");
                GameObject previousImported = importedModel;
                old.gameObject.SetActive(false);
                defaultModel.SetActive(false);
                currentAnimator = animator; importedModel = candidate; candidate = null; activeAvatarReference = reference;
                CurrentName = Path.GetFileNameWithoutExtension(path);
                if (previousImported != null) Retire(previousImported);
                bound = false; orbit?.RefreshPlayerVisual(); SetDiagnostic(null); Stage("complete"); return true;
            }
            catch (Exception failure)
            {
                Debug.Log($"[PlayerVRM] stage={stage} result=failed error={failure.GetType().Name} detail={failure.Message}", this);
                if (bound && this != null && old != null && visual != null)
                {
                    old.gameObject.SetActive(true); old.Update(0);
                    if (!adapter.TryBind(old, out string rollbackError))
                        Debug.LogError("[PlayerVRM] Visual rollback failed: " + rollbackError, this);
                    oldState.Restore(old);
                }
                if (this != null && !lifetime.IsCancellationRequested) SetDiagnostic(SafeReason(failure));
                return false;
            }
            finally
            {
                pendingSpring?.Dispose();
                if (candidate != null) Retire(candidate);
                IsLoading = false;
                if (this != null) Changed?.Invoke();
            }
        }
        public bool RestoreDefault()
        {
            if (!AdmitChange()) return false;
            if (!IsCustom)
            { bool saved = store.Select(null); SetDiagnostic(saved ? null : "Could not save avatar settings."); return saved; }
            Animator old = currentAnimator; var oldState = new AnimatorSnapshot(old);
            defaultModel.SetActive(true); defaultAnimator.gameObject.SetActive(true); defaultAnimator.Update(0);
            bool attempted = false;
            try
            {
                attempted = true;
                if (!adapter.TryBind(defaultAnimator, out string error)) throw new InvalidDataException(error);
                if (!store.Select(null)) throw new IOException("Could not save avatar settings.");
                var previous = importedModel; importedModel = null; currentAnimator = defaultAnimator; CurrentName = "Xingmang"; activeAvatarReference = null;
                Retire(previous); orbit?.RefreshPlayerVisual(); SetDiagnostic(null); return true;
            }
            catch (Exception failure)
            {
                if (attempted) { adapter.TryBind(old, out _); oldState.Restore(old); }
                defaultModel.SetActive(false); SetDiagnostic(SafeReason(failure)); return false;
            }
        }
        private void SetDiagnostic(string value)
        {
            Diagnostic = value;
            GetComponent<EmbodiedNPC.Perception.PerceivableEntity>()?.SetAppearanceDescription(AppearanceDescription);
            Changed?.Invoke();
        }
        private static string SafeReason(Exception failure) => failure switch
        {
            FileNotFoundException => "VRM file is missing. Copy a .vrm file into Avatars and scan again.",
            UnauthorizedAccessException => "Cannot read/write avatar files. Check UserData permissions.",
            OperationCanceledException => "Avatar import cancelled.",
            InvalidDataException => failure.Message,
            InvalidOperationException => failure.Message,
            IOException => "Avatar file or settings could not be read/written. " + (failure is EndOfStreamException ? "Incomplete file." : "Check the file and UserData directory."),
            _ => SettingsUiLanguage.T("VRM 解析或加载失败：", "VRM parse/load failed: ") + failure.Message
        };
        private static void Retire(GameObject model) { model.SetActive(false); Destroy(model); }
        private void OnDestroy() { lifetime.Cancel(); if (importedModel != null) Retire(importedModel); }

        private sealed class AnimatorSnapshot
        {
            private readonly int[] states;
            private readonly float[] times, weights;
            public AnimatorSnapshot(Animator a)
            {
                states = new int[a.layerCount]; times = new float[a.layerCount]; weights = new float[a.layerCount];
                for (int i = 0; i < states.Length; i++)
                { var s = a.GetCurrentAnimatorStateInfo(i); states[i] = s.fullPathHash; times[i] = s.normalizedTime; weights[i] = a.GetLayerWeight(i); }
            }
            public void Restore(Animator a)
            {
                for (int i = 0; i < states.Length; i++)
                { if (states[i] != 0) a.Play(states[i], i, times[i]); a.SetLayerWeight(i, weights[i]); }
                a.Update(0);
            }
        }
    }
}
