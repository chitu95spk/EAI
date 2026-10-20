using Newtonsoft.Json;

namespace EasyLabel.Core.Convert
{
    public sealed class CocoDataset
    {
        [JsonProperty("images")]
        public List<CocoImage> Images { get; } = new List<CocoImage>();

        [JsonProperty("annotations")]
        public List<CocoAnnotation> Annotations { get; } = new List<CocoAnnotation>();

        [JsonProperty("categories")]
        public List<CocoCategory> Categories { get; } = new List<CocoCategory>();
    }

    public sealed class CocoImage
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("file_name")]
        public string FileName { get; set; } = string.Empty;

        [JsonProperty("width")]
        public int Width { get; set; }

        [JsonProperty("height")]
        public int Height { get; set; }
    }

    public sealed class CocoAnnotation
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("image_id")]
        public int ImageId { get; set; }

        [JsonProperty("category_id")]
        public int CategoryId { get; set; }

        [JsonProperty("bbox")]
        public List<double> BBox { get; } = new List<double>();

        [JsonProperty("area")]
        public double Area { get; set; }

        [JsonProperty("iscrowd")]
        public int IsCrowd { get; set; }

        [JsonProperty("segmentation")]
        public List<List<double>> Segmentation { get; } = new List<List<double>>();
    }

    public sealed class CocoCategory
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;
    }
}
