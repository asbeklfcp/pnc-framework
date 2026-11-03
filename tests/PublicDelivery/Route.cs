using System;
namespace PncUpdaterValidation {
 public static class MetadataRoute {
  public const string Stable = "https://raw.githubusercontent.com/asbeklfcp/pnc-framework/main/updates/stable/";
  public const string Validation = "https://raw.githubusercontent.com/asbeklfcp/pnc-framework/updater-validation-0.1.1/updates/stable/";
  public static Uri Apply(Uri input) {
   if(input == null)return null;
   if(input.AbsoluteUri == Stable+"latest.xml")return new Uri(Validation+"latest.xml");
   if(input.AbsoluteUri == Stable+"history.xml")return new Uri(Validation+"history.xml");
   return input;
  }
 }
}
