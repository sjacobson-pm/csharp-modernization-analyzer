// Test fixture: Code that should trigger MOD004 (pattern matching)
using System;

namespace TestApp.Services
{
    public class ShapeCalculator
    {
        // Pattern 1: 'is' with cast
        public double CalculateArea(object shape)
        {
            if (shape is Circle)
            {
                var circle = (Circle)shape;
                return Math.PI * circle.Radius * circle.Radius;
            }

            if (shape is Rectangle)
            {
                var rect = (Rectangle)shape;
                return rect.Width * rect.Height;
            }

            return 0;
        }

        // Pattern 2: 'as' with null check
        public string Describe(object shape)
        {
            var circle = shape as Circle;
            if (circle != null)
            {
                return $"Circle with radius {circle.Radius}";
            }

            var rect = shape as Rectangle;
            if (rect != null)
            {
                return $"Rectangle {rect.Width}x{rect.Height}";
            }

            return "Unknown shape";
        }
    }

    public class Circle
    {
        public double Radius { get; set; }
    }

    public class Rectangle
    {
        public double Width { get; set; }
        public double Height { get; set; }
    }
}
