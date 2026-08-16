using System;
using System.Threading;

class Spinner
{
    static void Main()
    {
        string[] frames = { "|", "/", "-", "\\" };
        int index = 0;

        Console.WriteLine("회전 중... (15초)"); 

        for (int i = 0; i < 150; i++)
        {
            Console.Write($"\r{frames[index]} 돌아가는 중...");
            index = (index + 1) % frames.Length;
            Thread.Sleep(100);
        }

        Console.WriteLine("\r완료!");
    }
}
