// Expected output after MOD004 applied
using System;

namespace TestApp.Services
{
    public class ShapeCalculator
    {
        // Pattern 1: pattern matching with 'is'
        public double CalculateArea(object shape)
        {
            if (shape is Circle circle)
            {
                return Math.PI * circle.Radius * circle.Radius;
            }

            if (shape is Rectangle rect)
            {
                return rect.Width * rect.Height;
            }

            return 0;
        }

        // Pattern 2: pattern matching replaces 'as' + null check
        public string Describe(object shape)
        {
            if (shape is Circle circle)
            {
                return $"Circle with radius {circle.Radius}";
            }

            if (shape is Rectangle rect)
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
