﻿using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.DemoTeam.Attributes;

#if HAS_PACKAGE_DEMOTEAM_DIGITALHUMAN
using Unity.DemoTeam.DigitalHuman;
#endif
#if HAS_PACKAGE_UNITY_VFXGRAPH
using UnityEngine.VFX;
#endif

namespace Unity.DemoTeam.Hair
{
	[ExecuteAlways, SelectionBase]
	public class HairInstance : MonoBehaviour
	{
		public static HashSet<HairInstance> s_instances = new HashSet<HairInstance>();

		[Serializable]
		public struct SettingsRoots
		{
#if HAS_PACKAGE_DEMOTEAM_DIGITALHUMAN
			[ToggleGroup]
			public bool rootsAttach;
			[ToggleGroupItem]
			public SkinAttachmentTarget rootsAttachTarget;
			[HideInInspector]
			public PrimarySkinningBone rootsAttachTargetBone;//TODO move to StrandGroupInstance?
#endif

			public static readonly SettingsRoots defaults = new SettingsRoots()
			{
#if HAS_PACKAGE_DEMOTEAM_DIGITALHUMAN
				rootsAttach = false,
				rootsAttachTarget = null,
#endif
			};
		}

		[Serializable]
		public struct SettingsStrands
		{
			public enum StrandScale
			{
				Fixed,
				UniformMin,
				UniformMax,
			}

			public enum StrandRenderer
			{
				BuiltinLines,
				BuiltinStrips,
#if HAS_PACKAGE_UNITY_VFXGRAPH
				VFXGraph,//TODO
#endif
			}

			//public enum Simulation
			//{
			//	Disabled,
			//	Enabled,
			//	EnabledInPlaymode,
			//}

			public enum SimulationRate
			{
				Fixed30Hz,
				Fixed60Hz,
				Fixed120Hz,
				CustomTimeStep,
			}

			[LineHeader("Rendering")]

			[ToggleGroup]
			public bool strandMaterial;
			[ToggleGroupItem]
			public Material strandMaterialValue;
			public StrandRenderer strandRenderer;
#if HAS_PACKAGE_UNITY_VFXGRAPH
			[VisibleIf(nameof(strandRenderer), StrandRenderer.VFXGraph)]
			public VisualEffect strandOutputGraph;
#endif
			public ShadowCastingMode strandShadows;
			[RenderingLayerMask]
			public int strandLayers;

			[LineHeader("Proportions")]

			[Tooltip("Strand scale")]
			public StrandScale strandScale;
			[Range(0.070f, 100.0f), Tooltip("Strand diameter (in millimeters)")]
			public float strandDiameter;

			[LineHeader("Dynamics")]

			//[Tooltip("Simulation state")]
			//public Simulation simulation;
			[ToggleGroup, Tooltip("Enable simulation")]
			public bool simulation;
			[ToggleGroupItem, Tooltip("Simulation update rate")]
			public SimulationRate simulationRate;
			[ToggleGroupItem(withLabel = true), Tooltip("Enable simulation in Edit Mode")]
			public bool simulationInEditor;
			[VisibleIf(nameof(simulationRate), SimulationRate.CustomTimeStep), Tooltip("Simulation time step (in seconds)")]
			public float simulationTimeStep;
			[ToggleGroup, Tooltip("Enable minimum number of simulation steps per rendered frame")]
			public bool stepsMin;
			[ToggleGroupItem, Tooltip("Minimum number of simulation steps per rendered frame")]
			public int stepsMinValue;
			[ToggleGroup, Tooltip("Enable maximum number of simulation steps per rendered frame")]
			public bool stepsMax;
			[ToggleGroupItem, Tooltip("Maximum number of simulation steps per rendered frame")]
			public int stepsMaxValue;

			public static readonly SettingsStrands defaults = new SettingsStrands()
			{
				strandDiameter = 1.0f,
				strandScale = StrandScale.Fixed,

				strandMaterial = false,
				strandMaterialValue = null,
				strandRenderer = StrandRenderer.BuiltinLines,
				strandShadows = ShadowCastingMode.On,
				strandLayers = 0x0101,//TODO this is the HDRP default -- should decide based on active pipeline asset

				simulation = true,
				simulationRate = SimulationRate.Fixed60Hz,
				simulationInEditor = true,

				simulationTimeStep = 1.0f / 100.0f,
				stepsMin = false,
				stepsMinValue = 1,
				stepsMax = true,
				stepsMaxValue = 2,
			};
		}

		[Serializable]
		public struct StrandGroupInstance
		{
			public GameObject container;

			public GameObject rootContainer;
			public MeshFilter rootFilter;
#if HAS_PACKAGE_DEMOTEAM_DIGITALHUMAN
			public SkinAttachment rootAttachment;
#endif

			public GameObject strandContainer;
			public MeshFilter strandFilter;
			public MeshRenderer strandRenderer;

			[NonSerialized] public Material materialInstance;
			[NonSerialized] public Mesh meshInstanceLines;
			[NonSerialized] public Mesh meshInstanceStrips;
		}

		public HairAsset hairAsset;
		public bool hairAssetQuickEdit;

		public StrandGroupInstance[] strandGroupInstances;
		public string strandGroupInstancesChecksum;

		public SettingsRoots settingsRoots = SettingsRoots.defaults;
		public SettingsStrands settingsStrands = SettingsStrands.defaults;

		public HairSim.SolverSettings solverSettings = HairSim.SolverSettings.defaults;
		public HairSim.VolumeSettings volumeSettings = HairSim.VolumeSettings.defaults;
		public HairSim.DebugSettings debugSettings = HairSim.DebugSettings.defaults;

		public HairSim.SolverData[] solverData;
		public HairSim.VolumeData volumeData;

		[NonSerialized] public float accumulatedTime;
		[NonSerialized] public int stepsLastFrame;
		[NonSerialized] public float stepsLastFrameSmooth;
		[NonSerialized] public int stepsLastFrameSkipped;

		void OnEnable()
		{
			UpdateStrandGroupInstances();
			UpdateStrandGroupHideFlags();

			s_instances.Add(this);
		}

		void OnDisable()
		{
			ReleaseRuntimeData();

			s_instances.Remove(this);
		}

		void OnValidate()
		{
			volumeSettings.volumeGridResolution = (Mathf.Max(8, volumeSettings.volumeGridResolution) / 8) * 8;
		}

		void OnDrawGizmos()
		{
			if (strandGroupInstances != null)
			{
				// volume bounds
				Gizmos.color = Color.Lerp(Color.white, Color.clear, 0.5f);
				Gizmos.DrawWireCube(HairSim.GetVolumeCenter(volumeData), 2.0f * HairSim.GetVolumeExtent(volumeData));
			}
		}

		void OnDrawGizmosSelected()
		{
			if (strandGroupInstances != null)
			{
				foreach (var strandGroupInstance in strandGroupInstances)
				{
					// root bounds
					var rootFilter = strandGroupInstance.rootFilter;
					if (rootFilter != null)
					{
						var rootMesh = rootFilter.sharedMesh;
						if (rootMesh != null)
						{
							var rootBounds = rootMesh.bounds;

							Gizmos.color = Color.Lerp(Color.blue, Color.clear, 0.5f);
							Gizmos.matrix = rootFilter.transform.localToWorldMatrix;
							Gizmos.DrawWireCube(rootBounds.center, rootBounds.size);
						}
					}

#if false
					// strand bounds
					var strandFilter = strandGroupInstance.strandFilter;
					if (strandFilter != null)
					{
						var strandMesh = strandFilter.sharedMesh;
						if (strandMesh != null)
						{
							var strandBounds = strandMesh.bounds;

							Gizmos.color = Color.Lerp(Color.green, Color.clear, 0.5f);
							Gizmos.matrix = rootFilter.transform.localToWorldMatrix;
							Gizmos.DrawWireCube(strandBounds.center, strandBounds.size);
						}
					}
#endif
				}
			}
		}

		void Update()
		{
			UpdateStrandGroupInstances();
			UpdateAttachedState();
		}

		void LateUpdate()
		{
			var cmd = CommandBufferPool.Get(this.name);
			{
				if (InitializeRuntimeData(cmd))
				{
					UpdateSimulationState(cmd);
					UpdateRendererState();
					Graphics.ExecuteCommandBuffer(cmd);
				}
			}
			CommandBufferPool.Release(cmd);
		}

		void UpdateStrandGroupInstances()
		{
#if UNITY_EDITOR
			var isPrefabInstance = UnityEditor.PrefabUtility.IsPartOfPrefabInstance(this);
			if (isPrefabInstance)
			{
				if (hairAsset != null)
				{
					// did the asset change since the prefab was built?
					if (hairAsset.checksum != strandGroupInstancesChecksum)
					{
						var prefabPath = UnityEditor.PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(this);
						var prefabStage = UnityEditor.Experimental.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
						if (prefabStage != null && prefabStage.assetPath == prefabPath)
							return;// do nothing if prefab is already open

						Debug.LogFormat(this, "{0}: rebuilding governing prefab '{1}'...", this.name, prefabPath);

						var prefabContainer = UnityEditor.PrefabUtility.LoadPrefabContents(prefabPath);
						if (prefabContainer != null)
						{
							foreach (var prefabHairInstance in prefabContainer.GetComponentsInChildren<HairInstance>(includeInactive: true))
							{
								prefabHairInstance.UpdateStrandGroupInstances();
							}

							UnityEditor.PrefabUtility.SaveAsPrefabAsset(prefabContainer, prefabPath);
							UnityEditor.PrefabUtility.UnloadPrefabContents(prefabContainer);
						}

						ReleaseRuntimeData();
					}
				}
				return;
			}
#endif

			if (hairAsset != null)
			{
				if (hairAsset.checksum != strandGroupInstancesChecksum)
				{
					HairInstanceBuilder.BuildHairInstance(this, hairAsset);
					ReleaseRuntimeData();
				}
			}
			else
			{
				HairInstanceBuilder.ClearHairInstance(this);
				ReleaseRuntimeData();
			}
		}

		void UpdateStrandGroupHideFlags()
		{
			if (strandGroupInstances == null)
				return;

			var hideFlags = HideFlags.NotEditable;

			foreach (var strandGroupInstance in strandGroupInstances)
			{
				strandGroupInstance.container.hideFlags = hideFlags;
				strandGroupInstance.rootContainer.hideFlags = hideFlags;
				strandGroupInstance.strandContainer.hideFlags = hideFlags;
			}
		}

		void UpdateAttachedState()
		{
			if (strandGroupInstances == null)
				return;

#if HAS_PACKAGE_DEMOTEAM_DIGITALHUMAN
	#if UNITY_EDITOR
			var isPrefabInstance = UnityEditor.PrefabUtility.IsPartOfPrefabInstance(this);
			if (isPrefabInstance)
				return;
	#endif

			var attachmentsChanged = false;
			{
				foreach (var strandGroupInstance in strandGroupInstances)
				{
					var attachment = strandGroupInstance.rootAttachment;
					if (attachment != null && (attachment.target != settingsRoots.rootsAttachTarget || attachment.attached != settingsRoots.rootsAttach))
					{
						attachment.target = settingsRoots.rootsAttachTarget;

						if (attachment.target != null && settingsRoots.rootsAttach)
						{
							attachment.Attach(storePositionRotation: false);
						}
						else
						{
							attachment.Detach(revertPositionRotation: false);
							attachment.checksum0 = 0;
							attachment.checksum1 = 0;
						}

						attachmentsChanged = true;
					}
				}
			}

			if (attachmentsChanged && settingsRoots.rootsAttachTarget != null)
			{
				settingsRoots.rootsAttachTarget.CommitSubjectsIfRequired();
				settingsRoots.rootsAttachTargetBone = new PrimarySkinningBone(settingsRoots.rootsAttachTarget.transform);
	#if UNITY_EDITOR
				UnityEditor.EditorUtility.SetDirty(settingsRoots.rootsAttachTarget);
	#endif
			}
#endif
		}

		void UpdateSimulationState(CommandBuffer cmd)
		{
			DispatchStepAccumulated(cmd, Time.deltaTime);
		}

		void UpdateRendererState()
		{
			if (strandGroupInstances == null)
				return;

			for (int i = 0; i != strandGroupInstances.Length; i++)
			{
				switch (settingsStrands.strandRenderer)
				{
					case SettingsStrands.StrandRenderer.BuiltinLines:
					case SettingsStrands.StrandRenderer.BuiltinStrips:
						{
							UpdateRendererStateBuiltin(ref strandGroupInstances[i], solverData[i], hairAsset.strandGroups[i]);
						}
						break;

					case SettingsStrands.StrandRenderer.VFXGraph:
						{
							//TODO support output to vfx graph
						}
						break;
				}
			}
		}

		void UpdateRendererStateBuiltin(ref StrandGroupInstance strandGroupInstance, in HairSim.SolverData solverData, in HairAsset.StrandGroup strandGroup)
		{
			ref var meshFilter = ref strandGroupInstance.strandFilter;
			ref var meshRenderer = ref strandGroupInstance.strandRenderer;

			ref var materialInstance = ref strandGroupInstance.materialInstance;
			ref var meshInstanceLines = ref strandGroupInstance.meshInstanceLines;
			ref var meshInstanceStrips = ref strandGroupInstance.meshInstanceStrips;

			HairInstanceBuilder.CreateInstanceIfNull(ref meshInstanceLines, strandGroup.meshAssetLines, HideFlags.HideAndDontSave);
			HairInstanceBuilder.CreateInstanceIfNull(ref meshInstanceStrips, strandGroup.meshAssetStrips, HideFlags.HideAndDontSave);

			switch (settingsStrands.strandRenderer)
			{
				case SettingsStrands.StrandRenderer.BuiltinLines:
					meshFilter.sharedMesh = meshInstanceLines;
					break;
				case SettingsStrands.StrandRenderer.BuiltinStrips:
					meshFilter.sharedMesh = meshInstanceStrips;
					break;
			}

			//TODO better renderer bounds
			//meshFilter.sharedMesh.bounds = GetSimulationBounds(worldSquare: false, worldToLocalTransform: meshFilter.transform.worldToLocalMatrix);
			meshFilter.sharedMesh.bounds = GetSimulationBounds().WithTransform(meshFilter.transform.worldToLocalMatrix);

			var materialAsset = GetStrandMaterial();
			if (materialAsset != null)
			{
				if (materialInstance == null)
				{
					materialInstance = new Material(materialAsset);
					materialInstance.name += "(Instance)";
					materialInstance.hideFlags = HideFlags.HideAndDontSave;
				}
				else
				{
					if (materialInstance.shader != materialAsset.shader)
						materialInstance.shader = materialAsset.shader;

					materialInstance.CopyPropertiesFromMaterial(materialAsset);
				}
			}

			if (materialInstance != null)
			{
				meshRenderer.enabled = true;
				meshRenderer.sharedMaterial = materialInstance;
				meshRenderer.shadowCastingMode = settingsStrands.strandShadows;
				meshRenderer.renderingLayerMask = (uint)settingsStrands.strandLayers;

				HairSim.PushSolverData(materialInstance, solverData);

				CoreUtils.SetKeyword(materialInstance, "HAIR_VERTEX_DYNAMIC", true);
				CoreUtils.SetKeyword(materialInstance, "HAIR_VERTEX_STRIPS", settingsStrands.strandRenderer == SettingsStrands.StrandRenderer.BuiltinStrips);
			}
			else
			{
				meshRenderer.enabled = false;
			}
		}

		public Quaternion GetRootRotation(in StrandGroupInstance strandGroupInstance)
		{
#if HAS_PACKAGE_DEMOTEAM_DIGITALHUMAN
			if (settingsRoots.rootsAttach && settingsRoots.rootsAttachTarget != null)
			{
				return settingsRoots.rootsAttachTargetBone.skinningBone.rotation;
			}
#endif
			return strandGroupInstance.rootFilter.transform.rotation;
		}

		public static Bounds GetRootBounds(in StrandGroupInstance strandGroupInstance, Matrix4x4? worldTransform = null)
		{
			var rootLocalBounds = strandGroupInstance.rootFilter.sharedMesh.bounds;
			var rootLocalToWorld = strandGroupInstance.rootFilter.transform.localToWorldMatrix;
			{
				return rootLocalBounds.WithTransform((worldTransform != null) ? (worldTransform.Value * rootLocalToWorld) : rootLocalToWorld);
			}
		}

		public bool GetSimulationActive()
		{
			return settingsStrands.simulation && (settingsStrands.simulationInEditor || Application.isPlaying);
		}

		public float GetSimulationTimeStep()
		{
			switch (settingsStrands.simulationRate)
			{
				case SettingsStrands.SimulationRate.Fixed30Hz: return 1.0f / 30.0f;
				case SettingsStrands.SimulationRate.Fixed60Hz: return 1.0f / 60.0f;
				case SettingsStrands.SimulationRate.Fixed120Hz: return 1.0f / 120.0f;
				case SettingsStrands.SimulationRate.CustomTimeStep: return settingsStrands.simulationTimeStep;
				default: return 0.0f;
			}
		}

		public Bounds GetSimulationBounds(bool worldSquare = true, Matrix4x4? worldToLocalTransform = null)
		{
			Debug.Assert(worldSquare == false || worldToLocalTransform == null);

			var strandScale = GetStrandScale();
			var rootBounds = GetRootBounds(strandGroupInstances[0], worldToLocalTransform);
			var rootMargin = hairAsset.strandGroups[0].maxStrandLength * strandScale;

			for (int i = 1; i != strandGroupInstances.Length; i++)
			{
				rootBounds.Encapsulate(GetRootBounds(strandGroupInstances[i], worldToLocalTransform));
				rootMargin = Mathf.Max(hairAsset.strandGroups[i].maxStrandLength * strandScale, rootMargin);
			}

			rootMargin *= 1.5f;
			rootBounds.Expand(2.0f * rootMargin);

			if (worldSquare)
				return new Bounds(rootBounds.center, rootBounds.size.CMax() * Vector3.one);
			else
				return rootBounds;
		}

		public float GetStrandDiameter()
		{
			return settingsStrands.strandDiameter;
		}

		public float GetStrandScale()
		{
			switch (settingsStrands.strandScale)
			{
				default:
				case SettingsStrands.StrandScale.Fixed:
					{
						return 1.0f;
					}
				case SettingsStrands.StrandScale.UniformMin:
					{
						var lossyScaleAbs = this.transform.lossyScale.Abs();
						var lossyScaleAbsMin = lossyScaleAbs.CMin();
						return lossyScaleAbsMin;
					}
				case SettingsStrands.StrandScale.UniformMax:
					{
						var lossyScaleAbs = this.transform.lossyScale.Abs();
						var lossyScaleAbsMax = lossyScaleAbs.CMax();
						return lossyScaleAbsMax;
					}
			}
		}

		public Material GetStrandMaterial()
		{
			var mat = null as Material;

			if (mat == null && settingsStrands.strandMaterial)
				mat = settingsStrands.strandMaterialValue;

			if (mat == null && hairAsset != null)
				mat = hairAsset.settingsBasic.material;

			return mat;
		}

		public void DispatchStepAccumulated(CommandBuffer cmd, float dt)
		{
			var active = GetSimulationActive();
			var stepDT = GetSimulationTimeStep();

			// skip if inactive or time step zero
			if (stepDT == 0.0f || active == false)
			{
				stepsLastFrame = 0;
				stepsLastFrameSmooth = 0.0f;
				stepsLastFrameSkipped = 0;
				return;
			}

			// calc number of steps
			accumulatedTime += dt;

			var stepCountRT = (int)Mathf.Floor(accumulatedTime / stepDT);
			var stepCount = stepCountRT;
			{
				stepCount = Mathf.Max(stepCount, settingsStrands.stepsMin ? settingsStrands.stepsMinValue : stepCount);
				stepCount = Mathf.Min(stepCount, settingsStrands.stepsMax ? settingsStrands.stepsMaxValue : stepCount);
			}

			// always subtract the maximum (effectively clear accumulated if skipping frames)
			accumulatedTime -= Mathf.Max(stepCountRT, stepCount) * stepDT;

			if (accumulatedTime < 0.0f)
				accumulatedTime = 0.0f;

			// perform the steps
			for (int i = 0; i != stepCount; i++)
			{
				DispatchStep(cmd, stepDT);
			}

			// update counters
			stepsLastFrame = stepCount;
			stepsLastFrameSmooth = Mathf.Lerp(stepsLastFrameSmooth, stepsLastFrame, 1.0f - Mathf.Pow(0.01f, dt / 0.2f));
			stepsLastFrameSkipped = Mathf.Max(0, stepCountRT - stepCount);
		}

		public void DispatchStep(CommandBuffer cmd, float dt)
		{
			if (!InitializeRuntimeData(cmd))
				return;

			// get bounds and scale
			var simulationBounds = GetSimulationBounds();
			var strandDiameter = GetStrandDiameter();
			var strandScale = GetStrandScale();

			// update solver roots
			for (int i = 0; i != solverData.Length; i++)
			{
				var rootMesh = strandGroupInstances[i].rootFilter.sharedMesh;
				var rootTransform = strandGroupInstances[i].rootFilter.transform.localToWorldMatrix;
				var strandRotation = GetRootRotation(strandGroupInstances[i]);

				HairSim.UpdateSolverData(cmd, ref solverData[i], solverSettings, rootTransform, strandRotation, strandDiameter, strandScale, dt);
				HairSim.UpdateSolverRoots(cmd, solverData[i], rootMesh);
			}

			// update volume boundaries
			HairSim.UpdateVolumeBoundaries(cmd, ref volumeData, volumeSettings, simulationBounds);

			// pre-step volume if resolution changed
			if (HairSim.PrepareVolumeData(ref volumeData, volumeSettings.volumeGridResolution, halfPrecision: false))
			{
				HairSim.UpdateVolumeData(cmd, ref volumeData, volumeSettings, simulationBounds, strandDiameter, strandScale);
				HairSim.StepVolumeData(cmd, ref volumeData, volumeSettings, solverData);
			}

			// step solver data
			for (int i = 0; i != solverData.Length; i++)
			{
				HairSim.StepSolverData(cmd, ref solverData[i], solverSettings, volumeData);
			}

			// step volume data
			HairSim.UpdateVolumeData(cmd, ref volumeData, volumeSettings, simulationBounds, strandDiameter, strandScale);
			HairSim.StepVolumeData(cmd, ref volumeData, volumeSettings, solverData);

			// update renderers
			UpdateRendererState();
		}

		public void DispatchDraw(CommandBuffer cmd)
		{
			if (!InitializeRuntimeData(cmd))
				return;

			// draw solver data
			for (int i = 0; i != solverData.Length; i++)
			{
				HairSim.DrawSolverData(cmd, solverData[i], debugSettings);
			}

			// draw volume data
			HairSim.DrawVolumeData(cmd, volumeData, debugSettings);
		}

		bool InitializeRuntimeData(CommandBuffer cmd)
		{
			if (hairAsset == null)
				return false;

			if (hairAsset.checksum != strandGroupInstancesChecksum)
				return false;

			var strandGroups = hairAsset.strandGroups;
			if (strandGroups == null || strandGroups.Length == 0)
				return false;

			if (solverData != null && solverData.Length == strandGroups.Length)
				return true;

			// prep volume data
			HairSim.PrepareVolumeData(ref volumeData, volumeSettings.volumeGridResolution, halfPrecision: false);

			volumeData.allGroupsMaxParticleInterval = 0.0f;

			for (int i = 0; i != strandGroups.Length; i++)
			{
				volumeData.allGroupsMaxParticleInterval = Mathf.Max(volumeData.allGroupsMaxParticleInterval, strandGroups[i].maxParticleInterval);
			}

			// init solver data
			solverData = new HairSim.SolverData[strandGroups.Length];

			for (int i = 0; i != strandGroups.Length; i++)
			{
				ref var strandGroup = ref strandGroups[i];

				HairSim.PrepareSolverData(ref solverData[i], strandGroup.strandCount, strandGroup.strandParticleCount);

				solverData[i].memoryLayout = strandGroup.particleMemoryLayout;

				solverData[i].cbuffer._StrandCount = (uint)strandGroup.strandCount;
				solverData[i].cbuffer._StrandParticleCount = (uint)strandGroup.strandParticleCount;
				solverData[i].cbuffer._StrandMaxParticleInterval = strandGroup.maxParticleInterval;
				solverData[i].cbuffer._StrandMaxParticleWeight = strandGroup.maxParticleInterval / volumeData.allGroupsMaxParticleInterval;

				int strandGroupParticleCount = strandGroup.strandCount * strandGroup.strandParticleCount;

				using (var tmpRootPosition = new NativeArray<Vector4>(strandGroup.strandCount, Allocator.Temp, NativeArrayOptions.ClearMemory))
				using (var tmpRootDirection = new NativeArray<Vector4>(strandGroup.strandCount, Allocator.Temp, NativeArrayOptions.ClearMemory))
				using (var tmpParticlePosition = new NativeArray<Vector4>(strandGroupParticleCount, Allocator.Temp, NativeArrayOptions.ClearMemory))
				{
					unsafe
					{
						fixed (void* srcRootPosition = strandGroup.rootPosition)
						fixed (void* srcRootDirection = strandGroup.rootDirection)
						fixed (void* srcParticlePosition = strandGroup.particlePosition)
						{
							UnsafeUtility.MemCpyStride(tmpRootPosition.GetUnsafePtr(), sizeof(Vector4), srcRootPosition, sizeof(Vector3), sizeof(Vector3), strandGroup.strandCount);
							UnsafeUtility.MemCpyStride(tmpRootDirection.GetUnsafePtr(), sizeof(Vector4), srcRootDirection, sizeof(Vector3), sizeof(Vector3), strandGroup.strandCount);
							UnsafeUtility.MemCpyStride(tmpParticlePosition.GetUnsafePtr(), sizeof(Vector4), srcParticlePosition, sizeof(Vector3), sizeof(Vector3), strandGroupParticleCount);
						}
					}

					solverData[i].rootScale.SetData(strandGroup.rootScale);
					solverData[i].rootPosition.SetData(tmpRootPosition);
					solverData[i].rootDirection.SetData(tmpRootDirection);

					solverData[i].particlePosition.SetData(tmpParticlePosition);

					// NOTE: the rest of these buffers are initialized in KInitParticles
					//solverData[i].particlePositionPrev.SetData(tmpParticlePosition);
					//solverData[i].particlePositionCorr.SetData(tmpZero);
					//solverData[i].particleVelocity.SetData(tmpZero);
					//solverData[i].particleVelocityPrev.SetData(tmpZero);
				}

				var rootMesh = strandGroupInstances[i].rootFilter.sharedMesh;
				var rootTransform = strandGroupInstances[i].rootFilter.transform.localToWorldMatrix;

				var strandDiameter = GetStrandDiameter();
				var strandScale = GetStrandScale();
				var strandRotation = GetRootRotation(strandGroupInstances[i]);

				HairSim.UpdateSolverData(cmd, ref solverData[i], solverSettings, rootTransform, strandRotation, strandDiameter, strandScale, 1.0f);
				HairSim.UpdateSolverRoots(cmd, solverData[i], rootMesh);
				{
					HairSim.InitSolverParticles(cmd, solverData[i]);
				}
			}

			// init volume data
			{
				var simulationBounds = GetSimulationBounds();
				var strandDiameter = GetStrandDiameter();
				var strandScale = GetStrandScale();

				HairSim.UpdateVolumeData(cmd, ref volumeData, volumeSettings, simulationBounds, strandDiameter, strandScale);
				HairSim.StepVolumeData(cmd, ref volumeData, volumeSettings, solverData);

				for (int i = 0; i != solverData.Length; i++)
				{
					HairSim.InitSolverParticlesPostVolume(cmd, solverData[i], volumeData);
				}
			}

			// ready
			return true;
		}

		void ReleaseRuntimeData()
		{
			if (strandGroupInstances != null)
			{
				foreach (var strandGroupInstance in strandGroupInstances)
				{
					CoreUtils.Destroy(strandGroupInstance.materialInstance);
					CoreUtils.Destroy(strandGroupInstance.meshInstanceLines);
					CoreUtils.Destroy(strandGroupInstance.meshInstanceStrips);
				}
			}

			if (solverData != null)
			{
				for (int i = 0; i != solverData.Length; i++)
				{
					HairSim.ReleaseSolverData(ref solverData[i]);
				}

				solverData = null;
			}

			HairSim.ReleaseVolumeData(ref volumeData);
		}
	}

	public static class HairInstanceBuilder
	{
		public static void ClearHairInstance(HairInstance hairInstance)
		{
			if (hairInstance.strandGroupInstances != null)
			{
				foreach (var strandGroupInstance in hairInstance.strandGroupInstances)
				{
					CoreUtils.Destroy(strandGroupInstance.container);
					CoreUtils.Destroy(strandGroupInstance.materialInstance);
					CoreUtils.Destroy(strandGroupInstance.meshInstanceLines);
					CoreUtils.Destroy(strandGroupInstance.meshInstanceStrips);
				}

				hairInstance.strandGroupInstances = null;
				hairInstance.strandGroupInstancesChecksum = string.Empty;
			}

#if UNITY_EDITOR
			UnityEditor.EditorUtility.SetDirty(hairInstance);
#endif
		}

		public static void BuildHairInstance(HairInstance hairInstance, HairAsset hairAsset)
		{
			ClearHairInstance(hairInstance);

			var strandGroups = hairAsset.strandGroups;
			if (strandGroups == null || strandGroups.Length == 0)
				return;

			// prep strand group instances
			hairInstance.strandGroupInstances = new HairInstance.StrandGroupInstance[strandGroups.Length];
			hairInstance.strandGroupInstancesChecksum = hairAsset.checksum;

			// build strand group instances
			var hideFlags = HideFlags.NotEditable;

			for (int i = 0; i != strandGroups.Length; i++)
			{
				ref var strandGroupInstance = ref hairInstance.strandGroupInstances[i];

				strandGroupInstance.container = CreateContainer("Group:" + i, hairInstance.gameObject, hideFlags);

				// scene objects for roots
				strandGroupInstance.rootContainer = CreateContainer("Roots:" + i, strandGroupInstance.container, hideFlags);
				{
					strandGroupInstance.rootFilter = CreateComponent<MeshFilter>(strandGroupInstance.rootContainer, hideFlags);
					strandGroupInstance.rootFilter.sharedMesh = strandGroups[i].meshAssetRoots;

#if HAS_PACKAGE_DEMOTEAM_DIGITALHUMAN
					strandGroupInstance.rootAttachment = CreateComponent<SkinAttachment>(strandGroupInstance.rootContainer, hideFlags);
					strandGroupInstance.rootAttachment.attachmentType = SkinAttachment.AttachmentType.Mesh;
					strandGroupInstance.rootAttachment.forceRecalculateBounds = true;
#endif
				}

				// scene objects for strands
				strandGroupInstance.strandContainer = CreateContainer("Strands:" + i, strandGroupInstance.container, hideFlags);
				{
					strandGroupInstance.strandFilter = CreateComponent<MeshFilter>(strandGroupInstance.strandContainer, hideFlags);
					strandGroupInstance.strandRenderer = CreateComponent<MeshRenderer>(strandGroupInstance.strandContainer, hideFlags);
				}
			}

#if UNITY_EDITOR
			UnityEditor.EditorUtility.SetDirty(hairInstance);
#endif
		}

		//---------
		// utility

		public static GameObject CreateContainer(string name, GameObject parentContainer, HideFlags hideFlags)
		{
			var container = new GameObject(name);
			{
				container.transform.SetParent(parentContainer.transform, worldPositionStays: false);
				container.hideFlags = hideFlags;
			}
			return container;
		}

		public static T CreateComponent<T>(GameObject container, HideFlags hideFlags) where T : Component
		{
			var component = container.AddComponent<T>();
			{
				component.hideFlags = hideFlags;
			}
			return component;
		}

		public static void CreateComponentIfNull<T>(ref T component, GameObject container, HideFlags hideFlags) where T : Component
		{
			if (component == null)
				component = CreateComponent<T>(container, hideFlags);
		}

		public static T CreateInstance<T>(T original, HideFlags hideFlags) where T : UnityEngine.Object
		{
			var instance = UnityEngine.Object.Instantiate(original);
			{
				instance.name = original.name + "(Instance)";
				instance.hideFlags = hideFlags;
			}
			return instance;
		}

		public static void CreateInstanceIfNull<T>(ref T instance, T original, HideFlags hideFlags) where T : UnityEngine.Object
		{
			if (instance == null)
				instance = CreateInstance(original, hideFlags);
		}
	}
}