namespace Ruri.RipperHook.RuriEngine;

internal static class UsdMatrices
{
    private const int Size = 4;
    private static readonly string[] RowNames = ["X", "Y", "Z", "W"];

    public static double[] Invert(double[] matrix)
    {
        double[] work = (double[])matrix.Clone();
        double[] inverse = Identity();
        for (int column = 0; column < Size; column++)
        {
            int pivot = column;
            for (int row = column + 1; row < Size; row++)
            {
                if (Math.Abs(work[row * Size + column]) > Math.Abs(work[pivot * Size + column]))
                {
                    pivot = row;
                }
            }
            if (work[pivot * Size + column] == 0.0)
            {
                throw new InvalidOperationException("matrix is singular");
            }
            SwapRows(work, pivot, column);
            SwapRows(inverse, pivot, column);
            double divisor = work[column * Size + column];
            for (int element = 0; element < Size; element++)
            {
                work[column * Size + element] /= divisor;
                inverse[column * Size + element] /= divisor;
            }
            for (int row = 0; row < Size; row++)
            {
                double factor = work[row * Size + column];
                if (row == column || factor == 0.0)
                {
                    continue;
                }
                for (int element = 0; element < Size; element++)
                {
                    work[row * Size + element] -= factor * work[column * Size + element];
                    inverse[row * Size + element] -= factor * inverse[column * Size + element];
                }
            }
        }
        return inverse;
    }

    public static double[] Transpose(double[] matrix)
    {
        double[] result = new double[matrix.Length];
        for (int row = 0; row < Size; row++)
        {
            for (int column = 0; column < Size; column++)
            {
                result[column * Size + row] = matrix[row * Size + column];
            }
        }
        return result;
    }

    public static double[] ReflectX(double[] matrix)
    {
        double[] result = (double[])matrix.Clone();
        for (int row = 0; row < Size; row++)
        {
            for (int column = 0; column < Size; column++)
            {
                if ((row == 0) != (column == 0))
                {
                    result[row * Size + column] = -result[row * Size + column];
                }
            }
        }
        return result;
    }

    public static ObjectValue ToValue(double[] matrix)
    {
        ObjectValue result = new();
        for (int row = 0; row < Size; row++)
        {
            ObjectValue line = new();
            for (int column = 0; column < Size; column++)
            {
                line.Add(RowNames[column], ScalarValue.FromDouble(matrix[row * Size + column]));
            }
            result.Add(RowNames[row], line);
        }
        return result;
    }

    private static double[] Identity()
    {
        double[] result = new double[Size * Size];
        for (int index = 0; index < Size; index++)
        {
            result[index * Size + index] = 1.0;
        }
        return result;
    }

    private static void SwapRows(double[] matrix, int first, int second)
    {
        if (first == second)
        {
            return;
        }
        for (int element = 0; element < Size; element++)
        {
            (matrix[first * Size + element], matrix[second * Size + element]) = (matrix[second * Size + element], matrix[first * Size + element]);
        }
    }
}
