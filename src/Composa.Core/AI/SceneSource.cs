using Composa.Model;
using SkiaSharp;
using System.Buffers.Binary;

namespace Composa.AI;

/// <summary>Identifies the immutable source and retires only its previous lighting/shadow results.</summary>
public static class SceneSource
{
    public static string Tag(Guid id)=>"scene-source-"+id.ToString("N");
    public static IEnumerable<string> FrameTags(SKMatrix matrix)
    {
        var values=new[] {matrix.ScaleX,matrix.SkewX,matrix.TransX,matrix.SkewY,matrix.ScaleY,matrix.TransY,matrix.Persp0,matrix.Persp1,matrix.Persp2};
        for(var row=0;row<3;row++)
        {
            var bytes=new byte[12];for(var col=0;col<3;col++)BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(col*4,4),values[row*3+col]);
            yield return "scene-frame-"+row+"-"+Convert.ToHexString(bytes).ToLowerInvariant();
        }
    }
    public static SKMatrix Placement(Layer selected,Layer original)
    {
        if(!selected.Tags.Contains("scene-object"))return original.Matrix;
        var frame=original.Matrix;var values=new float[9];var valid=true;
        for(var row=0;row<3;row++)
        {
            var prefix="scene-frame-"+row+"-";var tag=selected.Tags.FirstOrDefault(value=>value.StartsWith(prefix,StringComparison.Ordinal));
            if(tag==null) {valid=false;break;}
            try
            {
                var bytes=Convert.FromHexString(tag[prefix.Length..]);if(bytes.Length!=12){valid=false;break;}
                for(var col=0;col<3;col++)values[row*3+col]=BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(col*4,4));
            }
            catch(FormatException){valid=false;break;}
        }
        if(valid&&values.All(float.IsFinite))frame=new(values[0],values[1],values[2],values[3],values[4],values[5],values[6],values[7],values[8]);
        return frame.PostConcat(selected.Matrix);
    }
    public static LayerTransform Transform(SKMatrix matrix,int width,int height)
    {
        var tl=matrix.MapPoint(0,0);var tr=matrix.MapPoint(width,0);var br=matrix.MapPoint(width,height);var bl=matrix.MapPoint(0,height);
        return LayerTransform.Identity(width,height) with {Distort=[tl.X,tl.Y,tr.X-width,tr.Y,br.X-width,br.Y-height,bl.X,bl.Y-height]};
    }
    public static Layer? SelectedObject(Document document)
    {
        var active=document.ActiveLayer;
        return active?.IsGroup==true ? Document.Flatten(active.Children).FirstOrDefault(layer=>layer.Tags.Contains("scene-object")&&document.IsEffectivelyVisible(layer)) : active;
    }
    public static Layer? Original(Document document,Layer? selected)
    {
        if(selected==null) return null;
        var tag=selected.Tags.FirstOrDefault(value=>value.StartsWith("scene-source-",StringComparison.Ordinal));
        if(tag!=null&&Guid.TryParseExact(tag[13..],"N",out var id)&&document.Find(id) is {Pixels:not null} original) return original;
        // Compatibility with earlier releases: a result folder was inserted directly above its hidden source.
        if(selected.Tags.Contains("scene-object"))
        {
            var folder=document.ParentOf(selected.Id);
            while(folder!=null&&folder.Tags.Contains("ai-generated"))
            {
                var siblings=document.SiblingsOf(folder.Id)!;var index=siblings.IndexOf(folder);
                if(index>0&&siblings[index-1] is {Visible:false,Pixels:not null} previous) return Original(document,previous);
                folder=document.ParentOf(folder.Id);
            }
        }
        return selected;
    }
    public static IReadOnlyList<Guid> PreviousResults(Document document,Layer selected,Layer original)
    {
        var tag=Tag(original.Id);
        if(selected.Tags.Contains("scene-object"))
        {
            if(!selected.Tags.Contains(tag))
            {
                // Old releases nested new results inside old ones and left ancestor shadows visible.
                return document.AllLayers().Where(layer=>
                {
                    if(layer.Tags.Contains("scene-object"))return Original(document,layer)?.Id==original.Id;
                    if(!layer.Tags.Contains("scene-shadow"))return false;
                    var parent=document.ParentOf(layer.Id);
                    var owner=parent?.Children.FirstOrDefault(child=>child.Tags.Contains("scene-object"));
                    return owner!=null&&Original(document,owner)?.Id==original.Id;
                }).Select(layer=>layer.Id).ToArray();
            }
            var folder=document.ParentOf(selected.Id);
            // Another copy of a result can share the original pixels. It is a separate visible object.
            if(folder==null)return [selected.Id];
            if(!folder.Tags.Contains("ai-generated")&&!folder.Tags.Contains("ai-variants")&&document.ParentOf(folder.Id)?.Tags.Contains("ai-variants")!=true)return [selected.Id];
            var members=Document.Flatten(folder.Children).Where(layer=>layer.Tags.Contains(tag)||!selected.Tags.Contains(tag)).ToArray();
            if(members.Count(layer=>layer.Tags.Contains("scene-object")&&document.IsEffectivelyVisible(layer))>1)
                return members.Where(layer=>layer.Id==selected.Id||layer.Tags.Contains("scene-shadow")&&layer.Matrix.Equals(selected.Matrix)).Select(layer=>layer.Id).ToArray();
            return members.Where(layer=>layer.Tags.Contains("scene-object")||layer.Tags.Contains("scene-shadow")).Select(layer=>layer.Id).ToArray();
        }
        return document.AllLayers().Where(layer=>layer.Tags.Contains(tag)&&(layer.Tags.Contains("scene-object")||layer.Tags.Contains("scene-shadow"))).Select(layer=>layer.Id).ToArray();
    }
}
