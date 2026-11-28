using System;
using System.IO;
using Lemora;
using Lemora.Model;
using Lemora.Service;
using Lemora.Service.DataSource;
using Shawn.Utils.Interface;

namespace Tests
{
    public static class TestInit
    {
        public static void Init()
        {
            AppPathHelper.Instance = new AppPathHelper(Environment.CurrentDirectory);
            if (File.Exists(AppPathHelper.Instance.ProfileJsonPath))
                File.Delete(AppPathHelper.Instance.ProfileJsonPath);
            if (File.Exists(AppPathHelper.Instance.SqliteDbDefaultPath))
                File.Delete(AppPathHelper.Instance.SqliteDbDefaultPath);



            IoC.GetByType = (type, key) =>
            {
                if (type == typeof(IDataService) || type == typeof(DataService))
                    return new DataService();
                if (type == typeof(ILanguageService) || type == typeof(LanguageService) || type == typeof(MockLanguageService))
                    return new MockLanguageService();
                if (type == typeof(Lemora.Service.Configuration))
                    return new Lemora.Service.Configuration();
                if (type == typeof(Lemora.Service.ConfigurationService))
                    return new ConfigurationService(new Configuration(), new KeywordMatchService());
                if (type == typeof(DataSourceService))
                    return new DataSourceService(new ProtocolConfigurationService(), new GlobalData(new ConfigurationService(new Lemora.Service.Configuration(), new KeywordMatchService())));
                return null;
            };
        }
    }
}
