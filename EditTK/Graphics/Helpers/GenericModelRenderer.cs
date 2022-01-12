﻿using EditTK.Graphics.Helpers.Internal;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Veldrid;

namespace EditTK.Graphics.Helpers
{



    /// <summary>
    /// Provides all information to draw/render a specific type of model
    /// <para>see <see cref="Draw(CommandList, GenericModel{TIndex, TVertex}, ResourceSet[])"/></para>
    /// </summary>
    /// <typeparam name="TIndex">The index type used in the IndexBuffer</typeparam>
    /// <typeparam name="TVertex">The vertex type used in the VertexBuffer
    ///                     <para>Note: All fields need to have a <see cref="VertexAttributeAtrribute"/></para>
    /// </typeparam>
    public class GenericModelRenderer<TIndex, TVertex> : GenericModelRendererBase<TIndex, TVertex>
        where TIndex : unmanaged
        where TVertex : unmanaged
    {

        /// <summary>
        /// Creates a new <see cref="GenericModelRenderer{TIndex, TVertex}"/>
        /// </summary>
        /// <param name="vertexShaderSource">The source for the vertex shader</param>
        /// <param name="fragmentShaderSource">The source for the fragment shader</param>
        /// <param name="uniformLayouts">The layouts of all uniform sets used in the shader(s), 
        /// the order should match the set slot in the shader!</param>
        /// <param name="outputDescription">Describes the color/depth outputs of the fragment shader</param>
        /// <param name="blendState">Describes the blending options when drawing</param>
        /// <param name="depthState">Describes the depth compare/write behaivior when drawing</param>
        /// <param name="rasterizerState">Describes misc. render options</param>
        public GenericModelRenderer(
            ShaderSource vertexShaderSource, ShaderSource fragmentShaderSource,
            ShaderUniformLayout[] uniformLayouts, OutputDescription outputDescription,
            BlendStateDescription? blendState = null, DepthStencilStateDescription? depthState = null,
            RasterizerStateDescription? rasterizerState = null)
            : base(vertexShaderSource, fragmentShaderSource, uniformLayouts, outputDescription, blendState, depthState, rasterizerState)
        {
        }

        protected override VertexLayoutDescription[] GetVertexLayouts() => 
            new [] { VertexFormatCache.GetVertexLayout<TVertex>() };

        /// <summary>
        /// Draws a <see cref="GenericModel{TIndex, TVertex}"/> 
        /// with the shaders and renderstate of this <see cref="GenericModelRenderer{TIndex, TVertex}"/>
        /// and the given resourceSets
        /// </summary>
        /// <param name="cl">The <see cref="CommandList"/> to use for all involved gpu commands</param>
        /// <param name="model">The model to draw</param>
        /// <param name="resourceSets">The resource sets to submit to the shader(s), 
        /// the order should match the set slot in the shader!</param>
        public void Draw(CommandList cl, GenericModel<TIndex, TVertex> model, params ResourceSet[] resourceSets)
        {
            EnsureResourcesCreated();

            model.EnsureResourcesCreated();

            cl.SetPipeline(_pipeline);


            for (int i = 0; i < resourceSets.Length; i++)
            {
                cl.SetGraphicsResourceSet((uint)i, resourceSets[i]);
            }

            cl.SetVertexBuffer(0, model.VertexBuffer);
            cl.SetIndexBuffer(model.IndexBuffer, model.IndexFormat);

            cl.DrawIndexed((uint)model.Indices.Length);
        }
    }
}