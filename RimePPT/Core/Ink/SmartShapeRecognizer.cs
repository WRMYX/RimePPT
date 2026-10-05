using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace RimePPT.Core.Ink;

/// <summary>等距采样后比较几何候选。模糊、开放或回折笔迹保持原样。</summary>
public static class SmartShapeRecognizer
{
    public static float HoldTolerance(InkDevice device) => device == InkDevice.Touch ? 8 : device == InkDevice.Pen ? 4 : 3;
    private sealed record Candidate(List<Vector2> Points, float Error);
    public static StrokeData? Recognize(StrokeData stroke, InkViewport viewport)
    {
        if (!viewport.IsValid || stroke.Dots.Count < 3) return null;
        var raw = new List<Vector2>();
        foreach (var dot in stroke.Dots)
        {
            var point = viewport.ToDip(dot);
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y)) return null;
            if (raw.Count == 0 || Vector2.DistanceSquared(raw[^1], point) > .04f) raw.Add(point);
        }
        if (raw.Count < 3) return null;
        float originalLength = Length(raw);
        var extent = new Vector2(raw.Max(p => p.X)-raw.Min(p => p.X), raw.Max(p => p.Y)-raw.Min(p => p.Y));
        // 先压缩微小抖动，避免密集触摸采样将周长虚增。
        raw = Simplify(raw, Math.Clamp(extent.Length() * .018f, .75f, 5));
        float length = Length(raw), chord = Vector2.Distance(raw[0], raw[^1]);
        if (length < 28) return null;
        var points = Resample(raw, length, 97);
        var candidates = new List<Candidate>();
        if (chord >= 28 && chord / originalLength >= .82f)
        {
            var mean = points.Aggregate(Vector2.Zero, (sum, p) => sum + p) / points.Count;
            float xx = 0, xy = 0, yy = 0;
            foreach (var p in points) { var v = p - mean; xx += v.X*v.X; xy += v.X*v.Y; yy += v.Y*v.Y; }
            float angle = .5f * MathF.Atan2(2*xy, xx-yy);
            var axis = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            if (Vector2.Dot(axis, points[^1]-points[0]) < 0) axis = -axis;
            var errors = points.Select(p => MathF.Abs(Cross(p - mean, axis))).ToArray();
            float backwards = 0;
            for (int i = 1; i < points.Count; i++) backwards += Math.Max(0, -Vector2.Dot(points[i] - points[i - 1], axis));
            float rms = MathF.Sqrt(errors.Average(x => x * x));
            if (rms <= Math.Max(2, chord * .035f) && errors.Max() <= Math.Max(6, chord * .10f) && backwards <= chord * .08f)
                candidates.Add(new(new() { points[0], points[^1] }, rms / chord));
        }
        var min = new Vector2(points.Min(p => p.X), points.Min(p => p.Y));
        var max = new Vector2(points.Max(p => p.X), points.Max(p => p.Y));
        var size = max - min; float diagonal = size.Length();
        if (size.X >= 24 && size.Y >= 24 && chord <= diagonal * .18f)
        {
            var polygon = Polygon(points, diagonal * .035f);
            // 圆角常被分成两段短边；再提取较粗候选，但仍须通过原始点拟合误差检查。
            if (polygon.Count >= 4) polygon = Polygon(points, diagonal * .065f);
            if (polygon.Count is 3 or 4)
            {
                bool valid = true;
                float sign = 0;
                for (int i = 0; i < polygon.Count; i++)
                {
                    var a = polygon[(i + 1) % polygon.Count] - polygon[i];
                    var b = polygon[(i + 2) % polygon.Count] - polygon[(i + 1) % polygon.Count];
                    float turn = Cross(a, b);
                    if (a.Length() < Math.Max(12, diagonal * .12f) || Math.Abs(turn) < diagonal * diagonal * .015f) valid = false;
                    if (i == 0) sign = Math.Sign(turn);
                    else if (Math.Sign(turn) != sign) valid = false;
                    if (polygon.Count == 4 && Math.Abs(Vector2.Dot(Vector2.Normalize(a), Vector2.Normalize(b))) > .28f) valid = false;
                }
                if (valid)
                {
                    if (polygon.Count == 4)
                    {
                        // 在拟合边方向的局部坐标中生成矩形，支持任意旋转。
                        var axis = Vector2.Normalize(polygon[1] - polygon[0]); var normal = new Vector2(-axis.Y, axis.X);
                        float l = points.Min(p => Vector2.Dot(p, axis)), r = points.Max(p => Vector2.Dot(p, axis));
                        float t = points.Min(p => Vector2.Dot(p, normal)), b = points.Max(p => Vector2.Dot(p, normal));
                        var corners = new List<Vector2> { axis*l+normal*t, axis*r+normal*t, axis*r+normal*b, axis*l+normal*b };
                        polygon = Order(corners, points[0], SignedArea(points));
                    }
                    float perimeter = Length(polygon.Concat(new[] { polygon[0] }).ToList());
                    var errors = points.Select(p => Enumerable.Range(0, polygon.Count).Min(i => Distance(p, polygon[i], polygon[(i+1)%polygon.Count]))).ToArray();
                    float rms = MathF.Sqrt(errors.Average(x => x*x));
                    if (length / perimeter is >= .78f and <= 1.25f && rms / diagonal < .035f && errors.Max() / diagonal < .09f)
                    { polygon.Add(polygon[0]); candidates.Add(new(polygon, rms / diagonal)); }
                }
            }
            if (size.X / size.Y is >= .7f and <= 1.43f)
            {
                var mean = points.Aggregate(Vector2.Zero, (sum,p) => sum+p) / points.Count;
                double xx=0,xy=0,yy=0,xq=0,yq=0;
                foreach (var p in points) { var v=p-mean; double q=v.LengthSquared(); xx+=v.X*v.X; xy+=v.X*v.Y; yy+=v.Y*v.Y; xq+=v.X*q*.5; yq+=v.Y*q*.5; }
                double det=xx*yy-xy*xy;
                if (Math.Abs(det) > 1e-6)
                {
                    var center=mean+new Vector2((float)((xq*yy-yq*xy)/det),(float)((yq*xx-xq*xy)/det));
                    float radius=points.Average(p=>Vector2.Distance(p,center));
                    var errors=points.Select(p=>Math.Abs(Vector2.Distance(p,center)-radius)).ToArray();
                    var bins=new HashSet<int>(points.Select(p => (int)((MathF.Atan2(p.Y-center.Y,p.X-center.X)+MathF.PI)*24/MathF.Tau)%24));
                    float rms=MathF.Sqrt(errors.Average(e=>e*e));
                    if (radius>=12 && bins.Count>=21 && rms/radius<=.095f && errors.Max()/radius<=.24f && length/radius is >= 5.2f and <= 7.5f)
                    {
                        float start=MathF.Atan2(points[0].Y-center.Y,points[0].X-center.X), direction=SignedArea(points)<0?-1:1;
                        var circle=Enumerable.Range(0,65).Select(i=>center+radius*new Vector2(MathF.Cos(start+direction*i*MathF.Tau/64),MathF.Sin(start+direction*i*MathF.Tau/64))).ToList();
                        candidates.Add(new(circle,rms/diagonal));
                    }
                }
            }
        }
        var ranked=candidates.OrderBy(x=>x.Error).ToArray();
        if (ranked.Length==0 || (ranked.Length>1 && ranked[1].Error-ranked[0].Error<.004f)) return null;
        return new StrokeData { Id=stroke.Id, SlideIndex=stroke.SlideIndex, Argb=(byte[])stroke.Argb.Clone(),
            ThicknessDips=stroke.ThicknessDips, LineStyle=stroke.LineStyle, Dots=ranked[0].Points.Select(viewport.Normalize).ToList() };
    }
    private static float Cross(Vector2 a,Vector2 b)=>a.X*b.Y-a.Y*b.X;
    private static float Length(IReadOnlyList<Vector2> p) { float n=0; for(int i=1;i<p.Count;i++)n+=Vector2.Distance(p[i-1],p[i]);return n; }
    private static float SignedArea(IReadOnlyList<Vector2> p) { float n=0; for(int i=0;i<p.Count;i++)n+=Cross(p[i],p[(i+1)%p.Count]);return n; }
    private static float Distance(Vector2 p,Vector2 a,Vector2 b) { var d=b-a;float t=d.LengthSquared()>0?Math.Clamp(Vector2.Dot(p-a,d)/d.LengthSquared(),0,1):0;return Vector2.Distance(p,a+t*d); }
    private static List<Vector2> Resample(List<Vector2> raw,float length,int count)
    {
        var result=new List<Vector2>{raw[0]}; int segment=1;float passed=0;
        for(int i=1;i<count-1;i++) { float target=length*i/(count-1);while(segment<raw.Count-1 && passed+Vector2.Distance(raw[segment-1],raw[segment])<target){passed+=Vector2.Distance(raw[segment-1],raw[segment]);segment++;}float step=Vector2.Distance(raw[segment-1],raw[segment]);result.Add(Vector2.Lerp(raw[segment-1],raw[segment],Math.Clamp((target-passed)/step,0,1))); }
        result.Add(raw[^1]);return result;
    }
    private static List<Vector2> Simplify(List<Vector2> p,float tolerance)
    {
        if(p.Count<3)return p;int index=0;float far=0;
        for(int i=1;i<p.Count-1;i++){float error=Distance(p[i],p[0],p[^1]);if(error>far){far=error;index=i;}}
        if(far<=tolerance)return new(){p[0],p[^1]};
        return Simplify(p.Take(index+1).ToList(),tolerance).SkipLast(1).Concat(Simplify(p.Skip(index).ToList(),tolerance)).ToList();
    }
    private static List<Vector2> Polygon(List<Vector2> points,float tolerance)
    {
        int split=Enumerable.Range(1,points.Count-1).MaxBy(i=>Vector2.DistanceSquared(points[0],points[i]));
        var p=Simplify(points.Take(split+1).ToList(),tolerance).SkipLast(1)
            .Concat(Simplify(points.Skip(split).Concat(new[]{points[0]}).ToList(),tolerance).SkipLast(1)).ToList();
        bool changed=true;while(changed && p.Count>3){changed=false;for(int i=0;i<p.Count;i++)if(Distance(p[i],p[(i+p.Count-1)%p.Count],p[(i+1)%p.Count])<=tolerance){p.RemoveAt(i);changed=true;break;}}
        return p;
    }
    private static List<Vector2> Order(List<Vector2> p,Vector2 start,float direction)
    {
        int first=Enumerable.Range(0,p.Count).MinBy(i=>Vector2.DistanceSquared(p[i],start));int step=Math.Sign(SignedArea(p))==Math.Sign(direction)?1:-1;
        return Enumerable.Range(0,p.Count).Select(i=>p[(first+step*i+p.Count*2)%p.Count]).ToList();
    }
}
