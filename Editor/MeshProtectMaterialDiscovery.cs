#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MeshProtect
{
    /// <summary>
    /// Materials that the selected avatar can acquire before build-time tools merge its inputs.
    /// Follow only material, clip and controller references owned by its components; never walk
    /// arbitrary asset dependencies or another GameObject's hierarchy.
    /// </summary>
    internal static class MeshProtectMaterialDiscovery
    {
        internal static List<Material> Collect(GameObject avatar)
        {
            var materials = new List<Material>();
            if (avatar == null) return materials;

            var seenMaterials = new HashSet<Material>();
            var seenClips = new HashSet<AnimationClip>();
            var seenControllers = new HashSet<RuntimeAnimatorController>();

            void AddMaterial(Material material)
            {
                if (material != null && seenMaterials.Add(material)) materials.Add(material);
            }

            void AddClip(AnimationClip clip)
            {
                if (clip == null || !seenClips.Add(clip)) return;
                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    if (!binding.propertyName.StartsWith("m_Materials", StringComparison.Ordinal)) continue;
                    var keys = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                    if (keys == null) continue;
                    foreach (var key in keys) AddMaterial(key.value as Material);
                }
            }

            void AddController(RuntimeAnimatorController controller)
            {
                if (controller == null || !seenControllers.Add(controller)) return;
                foreach (var clip in controller.animationClips) AddClip(clip);
            }

            void Reference(UnityEngine.Object reference)
            {
                if (reference is Material material) AddMaterial(material);
                else if (reference is AnimationClip clip) AddClip(clip);
                else if (reference is RuntimeAnimatorController controller) AddController(controller);
            }

            foreach (var component in avatar.GetComponentsInChildren<Component>(true))
            {
                if (component == null || component is Transform) continue;
                if (component is Renderer renderer)
                    foreach (var material in renderer.sharedMaterials) AddMaterial(material);
                if (component is Animator animator) AddController(animator.runtimeAnimatorController);
                if (component is Animation animation)
                {
                    AddClip(animation.clip);
                    foreach (AnimationState state in animation) AddClip(state.clip);
                }

                // Descriptor layers, MA Merge Animator and VRCFury's nested feature/actions all
                // serialize these references. Next (not NextVisible) includes hidden fields and
                // enters SerializeReference objects without knowing any vendor's type names.
                try
                {
                    using (var serialized = new SerializedObject(component))
                    using (var property = serialized.GetIterator())
                    {
                        var managedReferences = new HashSet<long>();
                        bool enterChildren = true;
                        while (property.Next(enterChildren))
                        {
                            enterChildren = property.propertyType != SerializedPropertyType.String;
                            if (property.propertyType == SerializedPropertyType.ManagedReference)
                                enterChildren = managedReferences.Add(property.managedReferenceId);
                            else if (property.propertyType == SerializedPropertyType.ObjectReference)
                                Reference(property.objectReferenceValue);
                        }
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[MeshProtect] Could not read material/animation references on " +
                                     $"'{component.name}' ({component.GetType().Name}): {e.Message}");
                }
            }

            return materials;
        }
    }
}
#endif
