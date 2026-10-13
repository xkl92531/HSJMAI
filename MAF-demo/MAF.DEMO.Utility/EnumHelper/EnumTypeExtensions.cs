using MAF.DEMO.Core;
using MAF.DEMO.Core.CustomAttribute;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;

namespace MAF.DEMO.Utility
{
    /// <summary>
    /// 枚举拓展类
    /// </summary>
    public static class EnumTypeExtensions
    {

        private static ConcurrentDictionary<string, string> dic = new ConcurrentDictionary<string, string>();

        // 枚举值字典缓存
        private static readonly ConcurrentDictionary<Type, Dictionary<int, string>> valueDict = new();

        /// <summary>
        /// 扩展方法，获得枚举的Description
        /// </summary>
        /// <param name="value">枚举值</param>
        /// <param name="nameInstend">当枚举没有定义DescriptionAttribute,是否用枚举名代替，默认使用</param>
        /// <returns>枚举的Description</returns>
        public static string GetDescription(this Enum value, bool nameInstend = true)
        {
            Type type = value.GetType();
            string fullName = type.FullName + "." + value.ToString();
            if (!dic.ContainsKey(fullName))
            {
                string name = Enum.GetName(type, value);
                if (name == null)
                {
                    return null;
                }
                FieldInfo field = type.GetField(name);
                DescriptionAttribute attribute = Attribute.GetCustomAttribute(field, typeof(DescriptionAttribute)) as DescriptionAttribute;
                if (attribute == null && nameInstend == true)
                {
                    return name;
                }
                var description = (attribute == null ? null : attribute.Description);
                dic.TryAdd(fullName, description);
                return description;

            }
            else
            {
                string description = "";
                dic.TryGetValue(fullName, out description);
                return description;
            }
        }

        /// <summary>
        /// 获取枚举对象Key与名称的字典（缓存）
        /// </summary>
        /// <param name="enumType"></param>
        /// <returns></returns>
        public static Dictionary<int, string> GetEnumDictionary(Type enumType)
        {
            if (!enumType.IsEnum)
                return null;

            Type type = enumType.GetType();
            string fullName = type.FullName + "." + type.ToString();
             
            return GetEnumDictionaryItems(enumType);;
        }

        /// <summary>
        /// 获取枚举对象Key与名称的字典
        /// </summary>
        /// <param name="enumType"></param>
        /// <returns></returns>
        private static Dictionary<int, string> GetEnumDictionaryItems(Type enumType)
        {
            // 获取类型的字段，初始化一个有限长度的字典
            FieldInfo[] enumFields = enumType.GetFields(BindingFlags.Public | BindingFlags.Static);
            Dictionary<int, string> enumDic = new(enumFields.Length);

            // 遍历字段数组获取key和name
            foreach (FieldInfo enumField in enumFields)
            {
                int intValue = (int)enumField.GetValue(enumType);

                if (!string.IsNullOrEmpty(CurrentUserInfo.Instance.Language))
                {
                    var lang = enumField.GetCustomAttribute<MyLanguageAttribute>();

                    switch (CurrentUserInfo.Instance.Language.ToLower())
                    {
                        case DataDictionary.Code.Language.ZHCN:
                            {
                                enumDic[intValue] = lang.CName;
                                break;
                            }
                        case DataDictionary.Code.Language.ZHEN:
                            {
                                enumDic[intValue] = lang.EName;
                                break;
                            }
                        case DataDictionary.Code.Language.ZHTW:
                            {
                                enumDic[intValue] = lang.TCName;
                                break;
                            }
                    }
                    if (!enumDic.Keys.Contains(intValue) || string.IsNullOrEmpty(enumDic[intValue]))
                    {
                        enumDic[intValue] = enumField.Name;
                    }
                }
                else
                {
                    enumDic[intValue] = enumField.Name;
                }

            }
            return enumDic;
        }

    }
}
