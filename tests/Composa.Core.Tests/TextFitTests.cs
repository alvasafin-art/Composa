using Composa.Model;
using Composa.Text;

namespace Composa.Core.Tests;

public class TextFitTests
{
    [Theory]
    [InlineData(TextAlignment.Left)]
    [InlineData(TextAlignment.Center)]
    [InlineData(TextAlignment.Right)]
    public void Fitting_preserves_full_content_and_uses_real_layout(TextAlignment alignment)
    {
        var text=string.Join(" ",Enumerable.Repeat("Объект должен помещаться в холст",20));
        var original=new TextStyle { Text=text,Size=72,Alignment=alignment };
        var fitted=TextFit.Within(original,320,200); var layout=new TextLayout(fitted);
        Assert.Equal(text,fitted.Text); Assert.InRange(fitted.Size,1,72); Assert.False(layout.Overflows);
        Assert.True(layout.Width<=320 && layout.Height<=200);
        Assert.All(layout.Lines,line=>Assert.True(line.X+line.VisibleWidth<=layout.Width-TextLayout.Padding+.5));
    }

    [Fact]
    public void Short_point_text_remains_point_text_and_explicit_paragraph_is_preserved()
    {
        var style=new TextStyle { Text="Hi",Size=24 };
        Assert.Equal(style,TextFit.Within(style,320,200));
        var paragraph=style with { BoxWidth=200,BoxHeight=100 };
        Assert.Equal(paragraph,TextFit.Within(paragraph,320,200));
        Assert.Throws<ArgumentException>(()=>TextFit.Within(style,8,8));
    }
}
