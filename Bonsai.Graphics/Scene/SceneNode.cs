using System;
using System.Collections.Generic;
using System.Numerics;
using Bonsai.Graphics.Rendering;

namespace Bonsai.Graphics.Scene
{
    /// <summary>
    /// A node in the retained scene graph (ADR 0002): a local transform,
    /// optional mesh + material, and children. Game objects own and update
    /// nodes; only the renderer traverses them.
    /// </summary>
    public sealed class SceneNode
    {
        private readonly List<SceneNode> children = new List<SceneNode>();

        public string Name { get; set; }
        public SceneNode Parent { get; private set; }
        public IReadOnlyList<SceneNode> Children { get { return children; } }

        /// <summary>Transform relative to the parent node.</summary>
        public Matrix4x4 LocalTransform { get; set; } = Matrix4x4.Identity;

        /// <summary>Hidden nodes (and their subtrees) are skipped by the renderer.</summary>
        public bool Visible { get; set; } = true;

        public Mesh Mesh { get; set; }
        public Material Material { get; set; }

        public SceneNode(string name = null)
        {
            Name = name;
        }

        public SceneNode AddChild(SceneNode child)
        {
            if (child.Parent != null)
                child.Parent.children.Remove(child);
            child.Parent = this;
            children.Add(child);
            return child;
        }

        public void RemoveChild(SceneNode child)
        {
            if (children.Remove(child))
                child.Parent = null;
        }

        public void RemoveFromParent()
        {
            if (Parent != null)
                Parent.RemoveChild(this);
        }

        /// <summary>World transform: local composed onto the parent chain (row-vector convention).</summary>
        public Matrix4x4 GetWorldTransform()
        {
            Matrix4x4 world = LocalTransform;
            for (SceneNode p = Parent; p != null; p = p.Parent)
                world *= p.LocalTransform;
            return world;
        }
    }
}
