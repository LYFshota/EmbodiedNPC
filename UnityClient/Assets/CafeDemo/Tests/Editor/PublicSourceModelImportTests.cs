using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;

namespace CafeDemo.Tests
{
    public sealed class PublicSourceModelImportTests
    {
        // The existing Editor tools live in Assembly-CSharp-Editor, outside this asmdef.
        private static Type Tool(string name) => AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType(name)).Single(t => t != null);

        [Test]
        public void SixPublicModelsHaveBoundHumanoidAvatarsMeshesMaterialsAndShaders()
        {
            Assert.DoesNotThrow(() => Tool("PublicSourceModels").GetMethod("Validate").Invoke(null, null));
        }

        [Test]
        public void HealthyModelsAreValidatedWithoutImportingArtifacts()
        {
            var validate = Tool("PublicSourceModelImport").GetMethod("ValidateBatch");
            validate.Invoke(null, null);
            var before = AssetDatabase.GlobalArtifactProcessedVersion;
            validate.Invoke(null, null);
            Assert.That(AssetDatabase.GlobalArtifactProcessedVersion, Is.EqualTo(before));
        }

        [Test]
        public void MissingImportHasAnExplicitDiagnostic()
        {
            var problem = Tool("PublicSourceModels").GetMethod("GetProblem")
                .Invoke(null, new object[] { "Assets/__NoSuchPublicModel.vrm" }) as string;
            Assert.That(problem, Does.Contain("Model root missing"));
        }
    }
}
