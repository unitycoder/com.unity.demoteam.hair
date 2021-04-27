#ifndef __HAIRVERTEX_HLSL__
#define __HAIRVERTEX_HLSL__

#pragma editor_sync_compilation

#ifndef HAIR_VERTEX_DYNAMIC
#define HAIR_VERTEX_DYNAMIC 0
#endif

#ifndef HAIR_VERTEX_STRIPS
#define HAIR_VERTEX_STRIPS 0
#endif

#ifndef HAIR_VERTEX_PREVIEW
#define HAIR_VERTEX_PREVIEW 0
#endif

#if HAIR_VERTEX_PREVIEW
	StructuredBuffer<float4> _ParticlePosition;
	uint _StrandCount;
	uint _StrandParticleCount;
	float _StrandDiameter;
	float _StrandScale;
#else
	#include "HairSimData.hlsl"
#endif
#include "HairSimDebugDrawUtility.hlsl"

#if LAYOUT_INTERLEAVED
  #define DECLARE_STRAND(x)							\
	const uint strandIndex = x;						\
	const uint strandParticleBegin = strandIndex;	\
	const uint strandParticleStride = _StrandCount;	\
	const uint strandParticleEnd = strandParticleBegin + strandParticleStride * _StrandParticleCount;
#else
  #define DECLARE_STRAND(x)													\
	const uint strandIndex = x;												\
	const uint strandParticleBegin = strandIndex * _StrandParticleCount;	\
	const uint strandParticleStride = 1;									\
	const uint strandParticleEnd = strandParticleBegin + strandParticleStride * _StrandParticleCount;
#endif

struct HairVertex
{
	float3 positionOS;
	float3 normalOS;
	float3 tangentOS;
	float3 bitangentOS;
	float2 rootUV;
	float2 strandUV;
	float3 strandNormalTS;
	float3 debugColor;
};

HairVertex GetHairVertex_Dynamic(in uint particleID, in float2 particleUV)
{
#if HAIR_VERTEX_STRIPS
	uint linearParticleIndex = particleID >> 1;
#else
	uint linearParticleIndex = particleID;
#endif

	DECLARE_STRAND(linearParticleIndex / _StrandParticleCount);
	const uint i = strandParticleBegin + (linearParticleIndex % _StrandParticleCount) * strandParticleStride;

	float3 p = _ParticlePosition[i].xyz;
	
	float3 r0 = (i == strandParticleBegin)
		? normalize(_ParticlePosition[i + strandParticleStride].xyz - p)
		: normalize(p - _ParticlePosition[i - strandParticleStride].xyz);

	float3 r1 = (i == strandParticleEnd - strandParticleStride)
		? normalize(p - _ParticlePosition[i - strandParticleStride].xyz)
		: normalize(_ParticlePosition[i + strandParticleStride].xyz - p);

	float3 positionWS = GetCameraRelativePositionWS(p);
	float3 viewDirWS = GetWorldSpaceNormalizeViewDir(positionWS);

	float3 bitangentWS = normalize(r0 + r1);
	float3 tangentWS = normalize(cross(bitangentWS, viewDirWS));
	float3 normalWS = cross(tangentWS, bitangentWS);

	//TODO motion
	//float3 motionWS = _ParticlePosition[i].xyz -  _ParticlePositionPrev[i].xyz

	HairVertex v;
	{
#if HAIR_VERTEX_STRIPS
		v.positionOS = TransformWorldToObject(positionWS + tangentWS * (_StrandDiameter * _StrandScale * (particleUV.x - 0.5)));
#else
		v.positionOS = TransformWorldToObject(positionWS);
#endif
		//TODO fix these breaking underh non-uniform scaling (maybe just replace with QMul)
		v.normalOS = TransformWorldToObjectDir(normalWS);
		v.tangentOS = TransformWorldToObjectDir(tangentWS);
		v.bitangentOS = TransformWorldToObjectDir(bitangentWS);

		v.rootUV = float2(0.0, 0.0);
		v.strandUV = particleUV;
		v.strandNormalTS.x = saturate(2.0 * particleUV.x - 1.0);
		v.strandNormalTS.y = 0.0;
		v.strandNormalTS.z = sqrt(max(1e-5, 1.0 - v.strandNormalTS.x * v.strandNormalTS.x));

		v.debugColor = ColorCycle(strandIndex, _StrandCount);
	}
	return v;
}

HairVertex GetHairVertex_Static(in float3 positionOS, in float3 normalOS, in float3 tangentOS)
{
	HairVertex v;
	{
		v.positionOS = positionOS;
		v.normalOS = normalOS;
		v.tangentOS = tangentOS;
		v.bitangentOS = normalize(cross(normalOS, tangentOS));
		v.rootUV = float2(0.0, 0.0);
		v.strandUV = float2(0.0, 0.0);
		v.strandNormalTS = float3(0.0, 0.0, 1.0);
		v.debugColor = float3(0.5, 0.5, 0.5);
	}
	return v;
}

void HairVertex_float(
	in uint in_particleID,
	in float2 in_particleUV,
	in float3 in_staticPositionOS,
	in float3 in_staticNormalOS,
	in float3 in_staticTangentOS,
	out float3 out_positionOS,
	out float3 out_normalOS,
	out float3 out_tangentOS,
	out float3 out_bitangentOS,
	out float2 out_rootUV,
	out float2 out_strandUV,
	out float3 out_strandNormalTS,
	out float3 out_debugColor)
{
#if HAIR_VERTEX_DYNAMIC
	HairVertex v = GetHairVertex_Dynamic(in_particleID, in_particleUV);
#else
	HairVertex v = GetHairVertex_Static(in_staticPositionOS, in_staticNormalOS, in_staticTangentOS);
#endif
	{
		out_positionOS = v.positionOS;
		out_normalOS = v.normalOS;
		out_tangentOS = v.tangentOS;
		out_bitangentOS = v.bitangentOS;
		out_rootUV = v.rootUV;
		out_strandUV = v.strandUV;
		out_strandNormalTS = v.strandNormalTS;
		out_debugColor = v.debugColor;
	}
}

#endif//__HAIRVERTEX_HLSL__