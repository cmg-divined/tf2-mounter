using Sandbox;

internal struct SourceSkinnedVertex
{
	[VertexLayout.Position]
	public Vector3 position;

	[VertexLayout.Normal]
	public Vector3 normal;

	[VertexLayout.Tangent]
	public Vector3 tangent;

	[VertexLayout.TexCoord]
	public Vector2 texcoord;

	[VertexLayout.BlendIndices]
	public Color32 blendIndices;

	[VertexLayout.BlendWeight]
	public Color32 blendWeights;

	public static readonly VertexAttribute[] Layout = new VertexAttribute[]
	{
		new VertexAttribute(VertexAttributeType.Position, VertexAttributeFormat.Float32),
		new VertexAttribute(VertexAttributeType.Normal, VertexAttributeFormat.Float32),
		new VertexAttribute(VertexAttributeType.Tangent, VertexAttributeFormat.Float32),
		new VertexAttribute(VertexAttributeType.TexCoord, VertexAttributeFormat.Float32, 2),
		new VertexAttribute(VertexAttributeType.BlendIndices, VertexAttributeFormat.UInt8, 4),
		new VertexAttribute(VertexAttributeType.BlendWeights, VertexAttributeFormat.UInt8, 4)
	};

	public SourceSkinnedVertex(Vector3 position, Vector3 normal, Vector3 tangent, Vector2 texcoord, Color32 blendIndices, Color32 blendWeights)
	{
		this.position = position;
		this.normal = normal;
		this.tangent = tangent;
		this.texcoord = texcoord;
		this.blendIndices = blendIndices;
		this.blendWeights = blendWeights;
	}
}
