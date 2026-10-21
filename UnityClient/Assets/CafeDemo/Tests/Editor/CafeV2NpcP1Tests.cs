using System;
using System.IO;
using System.Linq;
using CafeDemo;
using EmbodiedNPC.Animation;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Dialogue.Input;
using EmbodiedNPC.Events;
using EmbodiedNPC.Perception;
using EmbodiedNPC.World;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
namespace CafeDemo.Tests
{
    public sealed class CafeV2NpcP1Tests
    {
        Scene scene;
        GameObject[] roots;
        T[] All<T>() where T:Component => roots.SelectMany(r=>r.GetComponentsInChildren<T>(true)).ToArray();
        private bool ownsScene;
        [SetUp] public void Load()
        {
            scene = SceneManager.GetSceneByPath("Assets/Scenes/CafeDemoV2.unity");
            ownsScene = !scene.IsValid() || !scene.isLoaded;
            if (ownsScene) scene = EditorSceneManager.OpenScene("Assets/Scenes/CafeDemoV2.unity", OpenSceneMode.Additive);
            roots = scene.GetRootGameObjects();
        }
        [TearDown] public void Close(){if(ownsScene)EditorSceneManager.CloseScene(scene,true);}
        [Test] public void FourNpcs_HaveIndependentRuntimeAndPersistentIdentity()
        {
            var composers=All<CognitiveContextComposer>().Where(x=>x.gameObject.activeInHierarchy).ToArray();
            Assert.That(composers.Length,Is.EqualTo(4));
            Assert.That(composers.Select(x=>x.CharacterDefinition.CharacterId).Distinct().Count(),Is.EqualTo(4));
            var entities=composers.Select(x=>x.GetComponent<WorldEntity>()).ToArray();
            Assert.That(entities.Select(x=>x.EntityId).Distinct().Count(),Is.EqualTo(4));
            Assert.That(entities.All(x=>x.HasPersistentIdentity),Is.True);
            Assert.That(entities.Select(x=>x.PersistentIdentity.PersistentId).Distinct().Count(),Is.EqualTo(4));
            foreach(var composer in composers){Assert.That(composer.CharacterDefinition.TryValidateConfiguration(out var error),Is.True,error);Assert.That(composer.GetComponent<DialogueParticipant>(),Is.Not.Null);Assert.That(composer.GetComponent<CognitiveRunDriver>(),Is.Not.Null);Assert.That(composer.GetComponent<PerceivedStateStore>(),Is.Not.Null);}
            Assert.That(composers.Select(x=>x.GetComponent<PerceivedStateStore>()).Distinct().Count(),Is.EqualTo(4));
            Assert.That(entities.Single(x=>x.EntityId=="npc_cafe_01").GetComponent<CognitiveContextComposer>().CharacterDefinition.CharacterId,Is.EqualTo("cafe.merchant"));
            Assert.That(entities.Single(x=>x.EntityId=="npc_cafe_regular_01").GetComponent<CognitiveContextComposer>().CharacterDefinition.CharacterId,Is.EqualTo("cafe.regular_customer"));
            var interactor=All<PlayerDialogueInteractor>().Single();var candidates=new SerializedObject(interactor).FindProperty("candidates");
            Assert.That(candidates.arraySize,Is.EqualTo(4));Assert.That(Enumerable.Range(0,4).Select(i=>candidates.GetArrayElementAtIndex(i).objectReferenceValue).Distinct().Count(),Is.EqualTo(4));
            var receivers=new SerializedObject(All<AgentEventRouter>().Single()).FindProperty("receivers");
            var endpoints=Enumerable.Range(0,receivers.arraySize).Select(i=>receivers.GetArrayElementAtIndex(i).objectReferenceValue).ToArray();
            foreach(var entity in entities)Assert.That(endpoints,Does.Contain(entity.GetComponent<AgentEventReceiver>()),"All dialogue targets need an explicitly routable event endpoint.");
            var registry=All<EntityRegistry>().Single();registry.RebuildRegistry();Assert.That(registry.IsReady,Is.True);
            foreach(var entity in entities)Assert.That(registry.TryGetEntity(entity.EntityId,out var found)&&found==entity,Is.True);
        }
        [Test] public void SeatedHumanoids_KeepActorRootsAndFacingFixed_AndHaveNoStaffActions()
        {
            var customers=All<CognitiveContextComposer>().Where(x=>x.gameObject.activeInHierarchy&&x.CharacterDefinition.CharacterId!="cafe.merchant").ToArray();
            Assert.That(customers.Length,Is.EqualTo(3));
            var expected=new[]{new Vector3(-4.426427f,.03f,.067218f),new Vector3(-2.931287f,.03f,.090797f),new Vector3(.722806f,.03f,1.988254f)};
            foreach(var customer in customers){
                var actor=customer.transform;Assert.That(expected.Min(x=>Vector3.Distance(x,actor.position)),Is.LessThan(.001));
                Assert.That(customer.GetComponent<AgentActionCapabilityProfile>().AllowedActionIds,Is.Empty);
                var animator=customer.GetComponentsInChildren<Animator>().Single();Assert.That(animator.avatar.isValid&&animator.avatar.isHuman,Is.True);Assert.That(animator.applyRootMotion,Is.False);
                var start=actor.position;var rotation=actor.rotation;var visual=actor.Find("Visual");var neutral=visual.localRotation;
                var facing=visual.GetComponent<FacingController>();Assert.That(facing.MaxVisualYawDegrees,Is.Zero);
                var target=new GameObject("P1 test speaker");try{
                    facing.Configure(visual,actor,customer.GetComponent<MotionChannelCoordinator>());target.transform.position=actor.position+Vector3.right;facing.SetDialogueTarget(target.transform,true);
                    animator.Rebind();animator.Update(0);
                    for(int frame=0;frame<180;frame++){animator.Update(1f/60);facing.Tick(1f/60);Assert.That(Vector3.Distance(actor.position,start),Is.LessThan(.0001));Assert.That(Quaternion.Angle(actor.rotation,rotation),Is.LessThan(.0001));Assert.That(Quaternion.Angle(visual.localRotation,neutral),Is.LessThan(.0001));}
                    Assert.That(animator.GetBoneTransform(HumanBodyBones.Hips).position.y,Is.InRange(.50f,.59f));
                }finally{UnityEngine.Object.DestroyImmediate(target);}
            }
        }
        [Test] public void SettingsDiscoverFour_OverridesReloadIndependently_AndLegacyOverrideIsPreserved()
        {
            var settings=All<CafeNpcCharacterSettings>().Single();settings.RefreshCandidates();Assert.That(settings.CandidateCount,Is.EqualTo(4));
            var definitions=Enumerable.Range(0,4).Select(i=>settings.GetCandidate(i).CharacterDefinition).ToArray();
            var dir=Path.Combine(Path.GetTempPath(),"cafe-p1-settings-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);var path=Path.Combine(dir,CharacterOverrideStore.FileName);
            try{
                File.WriteAllText(path,"{\"schema_version\":1,\"characters\":[{\"character_id\":\"cafe.regular_customer\",\"display_name\":\"原有顾客名字\",\"role_summary\":\"原有身份\",\"speaking_style\":\"原有风格\"}]}");
                var original=File.ReadAllBytes(path);var store=new CharacterOverrideStore(path);
                foreach(var definition in definitions)CharacterProfileResolver.Resolve(definition,store);
                Assert.That(File.ReadAllBytes(path),Is.EqualTo(original));
                var legacy=CharacterProfileResolver.Resolve(definitions.Single(x=>x.CharacterId=="cafe.regular_customer"),store);Assert.That(legacy.display_name,Is.EqualTo("原有顾客名字"));
                foreach(var definition in definitions.Where(x=>x.CharacterId!="cafe.regular_customer"))store.Save(definition.CharacterId,"自定义"+definition.DisplayName,definition.RoleSummary,definition.Backstory,definition.Personality,definition.SpeakingStyle,definition.Boundaries);
                var reloaded=new CharacterOverrideStore(path);
                Assert.That(CharacterProfileResolver.Resolve(definitions.Single(x=>x.CharacterId=="cafe.regular_customer"),reloaded).display_name,Is.EqualTo("原有顾客名字"));
                foreach(var definition in definitions.Where(x=>x.CharacterId!="cafe.regular_customer"))Assert.That(CharacterProfileResolver.Resolve(definition,reloaded).display_name,Is.EqualTo("自定义"+definition.DisplayName));
                reloaded.Remove("cafe.companion_customer");var restored=new CharacterOverrideStore(path);
                Assert.That(CharacterProfileResolver.Resolve(definitions.Single(x=>x.CharacterId=="cafe.companion_customer"),restored).display_name,Is.EqualTo("温予安"));
                Assert.That(CharacterProfileResolver.Resolve(definitions.Single(x=>x.CharacterId=="cafe.quiet_customer"),restored).display_name,Is.EqualTo("自定义江亦舟"));
            }finally{Directory.Delete(dir,true);}
        }
    }
}
