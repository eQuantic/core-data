using System;

namespace eQuantic.Core.Data.Migration;

public class MigrationAttribute : Attribute
{
    public string Title { get; private set; }
    public DateTime Date { get; private set; }

    public MigrationAttribute(string title, int year, int month, int day, int hour, int mintute, int second)
    {
        Title = title;
        Date = new DateTime(year, month, day, hour, mintute, second);
    }
}